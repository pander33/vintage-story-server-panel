using System.Text.Json.Nodes;
using Panel.Access.Domain;
using Panel.Access.Infrastructure;
using Panel.Access.Passwords;
using Panel.SharedKernel;

namespace Panel.Access.Tests;

public sealed class FileAccountStoreTests : IDisposable
{
	private readonly string root = Path.Combine(
		Path.GetTempPath(), "vs-panel-net-tests", Guid.NewGuid().ToString("N"));

	private static readonly PasswordHash password = PasswordHash.Create("пароль достаточной длины").Value;

	public FileAccountStoreTests() => Directory.CreateDirectory(root);

	public void Dispose()
	{
		try
		{
			Directory.Delete(root, recursive: true);
		}
		catch (IOException)
		{
			// Ошибка уборки временного каталога не влияет на результат теста.
		}
	}

	[Fact]
	public async Task Файл_панели_1_2_читается()
	{
		var path = Write($$"""
        {
          "version": 1,
          "users": {
            "admin": {"password": "{{password.Encode()}}", "role": "admin",
                      "totp_secret": "", "totp_enabled": false, "generation": 4},
            "гость": {"password": "{{password.Encode()}}", "role": "viewer",
                      "totp_enabled": true, "totp_secret": "GEZDGNBVGY3TQOJQ", "generation": 1}
          }
        }
        """);

		var directory = await Store(path).LoadAsync(CancellationToken.None);

		Assert.True(directory.IsSuccess);
		Assert.Equal(2, directory.Value.All.Count);
		Assert.Equal(4, directory.Value.Find(UserName.Administrator)!.Generation);
		Assert.True(directory.Value.Find(UserName.Parse("гость"))!.TotpEnabled);
	}

	/// <summary>
	/// Запись новой панелью не стирает неизвестные ей поля, иначе обновление теряет данные, а
	/// откат (ADR-15) бесполезен.
	/// </summary>
	[Fact]
	public async Task Неизвестные_поля_переживают_запись()
	{
		var path = Write($$"""
        {
          "version": 1,
          "users": {
            "admin": {
              "password": "{{password.Encode()}}",
              "role": "admin",
              "generation": 1,
              "будущее_поле": {"вложенное": 42}
            }
          }
        }
        """);

		var store = Store(path);
		var directory = await store.LoadAsync(CancellationToken.None);
		await store.SaveAsync(directory.Value, CancellationToken.None);

		var written = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
		Assert.Equal(42, (int?) written["users"]!["admin"]!["будущее_поле"]!["вложенное"]);
	}

	[Fact]
	public async Task Запись_и_чтение_дают_тот_же_справочник()
	{
		var path = Path.Combine(root, "accounts.json");
		var store = Store(path);

		var directory = new AccountDirectory([]).Put(UserName.Administrator, password, Role.Admin).Value;
		var saved = await store.SaveAsync(directory, CancellationToken.None);
		Assert.True(saved.IsSuccess, saved.Failure?.Message);

		var loaded = await store.LoadAsync(CancellationToken.None);
		Assert.True(loaded.Value.Find(UserName.Administrator)!.Password.Verify("пароль достаточной длины"));
	}

	/// <summary>
	/// Устаревший файл пароля переносится с поколением 0, как у уже выданных сессий.
	/// </summary>
	[Fact]
	public async Task Пароль_панели_0_9_переносится_с_поколением_ноль()
	{
		var legacy = Path.Combine(root, "admin.password");
		await File.WriteAllTextAsync(legacy, password.Encode() + "\n");

		var directory = await new FileAccountStore(Path.Combine(root, "accounts.json"), legacy)
			.LoadAsync(CancellationToken.None);

		var admin = Assert.Single(directory.Value.All);
		Assert.Equal(UserName.Administrator, admin.Name);
		Assert.Equal(Role.Admin, admin.Role);
		Assert.Equal(0, admin.Generation);
	}

	[Fact]
	public async Task Отсутствие_обоих_файлов_означает_пустой_справочник()
	{
		var directory = await Store(Path.Combine(root, "нет.json")).LoadAsync(CancellationToken.None);

		Assert.True(directory.IsSuccess);
		Assert.True(directory.Value.IsEmpty);
	}

	/// <summary>
	/// Испорченный файл не означает, что учётных записей нет, иначе на работающей панели
	/// открылся бы мастер установки.
	/// </summary>
	[Fact]
	public async Task Повреждённый_файл_даёт_отказ_а_не_пустой_справочник()
	{
		var path = Write("{ это не json");

		var directory = await Store(path).LoadAsync(CancellationToken.None);

		Assert.False(directory.IsSuccess);
		Assert.Equal(FailureCode.Unavailable, directory.Failure!.Code);
	}

	[Fact]
	public async Task Запись_с_негодным_хешем_пропускается_а_остальные_остаются()
	{
		var path = Write($$"""
        {
          "version": 1,
          "users": {
            "сломанный": {"password": "не-хеш", "role": "admin"},
            "admin": {"password": "{{password.Encode()}}", "role": "admin", "generation": 1}
          }
        }
        """);

		var directory = await Store(path).LoadAsync(CancellationToken.None);

		var only = Assert.Single(directory.Value.All);
		Assert.Equal(UserName.Administrator, only.Name);
	}

	private string Write(string json)
	{
		var path = Path.Combine(root, "accounts.json");
		File.WriteAllText(path, json);
		return path;
	}

	private FileAccountStore Store(string path) =>
		new(path, Path.Combine(root, "admin.password"));
}
