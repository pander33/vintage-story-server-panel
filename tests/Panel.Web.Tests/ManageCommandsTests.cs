using System.Text.Json.Nodes;
using Panel.Access.Domain;
using Panel.Access.Infrastructure;
using Panel.Provisioning.Domain;
using Panel.Web;

namespace Panel.Web.Tests;

/// <summary>
/// <c>vs-panel-web manage</c>: всё, что установщик поручает учётной записи панели. На временном
/// каталоге состояния, без демона.
/// </summary>
public sealed class ManageCommandsTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-manage-" + Guid.NewGuid().ToString("n"));

	private PanelOptions Options => new() { StateDirectory = directory, DefaultInstance = "main", Bind = "0.0.0.0", Port = 8181 };

	public void Dispose()
	{
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void Инициализация_создаёт_ключ_сессий_один_раз()
	{
		Assert.True(ManageCommands.Initialise(Options, TextWriter.Null).IsSuccess);
		var first = File.ReadAllBytes(Options.SecretKeyPath);

		Assert.True(ManageCommands.Initialise(Options, TextWriter.Null).IsSuccess);

		Assert.Equal(48, first.Length);
		Assert.Equal(first, File.ReadAllBytes(Options.SecretKeyPath));
		Assert.True(Directory.Exists(Options.StagingDirectory));
	}

	/// <summary>Файлы устаревшей раскладки переносятся к серверу main; существующий файл назначения не заменяется.</summary>
	[Fact]
	public void Состояние_09_переезжает_к_серверу_main()
	{
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "metrics.jsonl"), "старые метрики");
		File.WriteAllText(Path.Combine(directory, "updates.json"), "{}");
		File.WriteAllText(Path.Combine(directory, "accounts.json"), "{}");
		var main = Path.Combine(directory, "instances", "main");
		Directory.CreateDirectory(main);
		File.WriteAllText(Path.Combine(main, "updates.json"), "новое");
		var output = new StringWriter();

		ManageCommands.Initialise(Options, output);

		Assert.Equal("старые метрики", File.ReadAllText(Path.Combine(main, "metrics.jsonl")));
		Assert.False(File.Exists(Path.Combine(directory, "metrics.jsonl")));
		Assert.Equal("новое", File.ReadAllText(Path.Combine(main, "updates.json")));
		Assert.True(File.Exists(Path.Combine(directory, "updates.json")));
		Assert.True(File.Exists(Path.Combine(directory, "accounts.json")));
		Assert.Contains("metrics.jsonl", output.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void Второй_сервер_не_присваивает_состояние_09()
	{
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "metrics.jsonl"), "старые метрики");

		ManageCommands.Initialise(Options with { DefaultInstance = "creative" }, TextWriter.Null);

		Assert.True(File.Exists(Path.Combine(directory, "metrics.jsonl")));
		Assert.False(Directory.Exists(Path.Combine(directory, "instances")));
	}

	/// <summary>Токен из 32 символов base64url, как <c>secrets.token_urlsafe(24)</c>; ссылка на 127.0.0.1.</summary>
	[Fact]
	public async Task Токен_захвата_пишется_и_печатается_ссылкой()
	{
		var output = new StringWriter();

		var issued = await ManageCommands.IssueClaimTokenAsync(Options, output, CancellationToken.None);

		Assert.True(issued.IsSuccess);
		var token = File.ReadAllText(Options.SetupTokenPath).TrimEnd('\n');
		Assert.Matches("^[A-Za-z0-9_-]{32}$", token);
		Assert.Contains($"http://127.0.0.1:8181/setup?token={token}", output.ToString(), StringComparison.Ordinal);
		Assert.NotNull(ClaimToken.Create(File.ReadAllText(Options.SetupTokenPath)));
	}

	/// <summary>
	/// Панель слушает все адреса — ссылку печатают и по адресам машины в сети: с сервера без
	/// рабочего стола мастер открывают с другого компьютера.
	/// </summary>
	[Fact]
	public void Ссылка_мастера_по_адресам_в_сети()
	{
		var machine = new[] { System.Net.IPAddress.Loopback, System.Net.IPAddress.Parse("192.168.1.10") };

		Assert.Equal(["http://192.168.1.10:8181/setup?token=abc"], ManageCommands.NetworkSetupLinks(Options, "abc", machine));
		Assert.Empty(ManageCommands.NetworkSetupLinks(Options with { Bind = "127.0.0.1" }, "abc", machine));
	}

	[Fact]
	public async Task Новый_токен_отменяет_прежний()
	{
		await ManageCommands.IssueClaimTokenAsync(Options, TextWriter.Null, CancellationToken.None);
		var first = File.ReadAllText(Options.SetupTokenPath);

		await ManageCommands.IssueClaimTokenAsync(Options, TextWriter.Null, CancellationToken.None);

		Assert.NotEqual(first, File.ReadAllText(Options.SetupTokenPath));
	}

	[Fact]
	public async Task Токена_нет_после_завершения_мастера()
	{
		Directory.CreateDirectory(directory);
		File.WriteAllText(Options.SetupStatePath, new JsonObject { ["completed"] = true }.ToJsonString());

		var issued = await ManageCommands.IssueClaimTokenAsync(Options, TextWriter.Null, CancellationToken.None);

		Assert.False(issued.IsSuccess);
		Assert.False(File.Exists(Options.SetupTokenPath));
	}

	[Fact]
	public async Task Токена_нет_если_администратор_уже_есть()
	{
		await ManageCommands.SetPasswordAsync(Options, ("правильный пароль", "правильный пароль"), TextWriter.Null, CancellationToken.None);

		var issued = await ManageCommands.IssueClaimTokenAsync(Options, TextWriter.Null, CancellationToken.None);

		Assert.False(issued.IsSuccess);
		Assert.Contains("vs-panel password", issued.Failure!.Message, StringComparison.Ordinal);
	}

	/// <summary>Пароль создаёт admin, повторная смена увеличивает поколение, и прежние сессии перестают действовать.</summary>
	[Fact]
	public async Task Пароль_создаёт_admin_и_поднимает_поколение()
	{
		Assert.True((await ManageCommands.SetPasswordAsync(Options, ("первый пароль!", "первый пароль!"), TextWriter.Null, CancellationToken.None)).IsSuccess);
		Assert.True((await ManageCommands.SetPasswordAsync(Options, ("второй пароль!", "второй пароль!"), TextWriter.Null, CancellationToken.None)).IsSuccess);

		var directoryOfAccounts = (await new FileAccountStore(Options.AccountsPath, Options.LegacyPasswordPath).LoadAsync(CancellationToken.None)).Value;
		var admin = directoryOfAccounts.Find(UserName.Administrator)!;
		Assert.Equal(Role.Admin, admin.Role);
		Assert.Equal(2, admin.Generation);
		Assert.True(admin.Password.Verify("второй пароль!"));
	}

	[Theory]
	[InlineData("пароль длинный", "другой пароль!", "Пароли не совпадают")]
	[InlineData("короткий", "короткий", "не менее 12")]
	public async Task Неверный_пароль_не_сохраняется(string first, string second, string expected)
	{
		var result = await ManageCommands.SetPasswordAsync(Options, (first, second), TextWriter.Null, CancellationToken.None);

		Assert.False(result.IsSuccess);
		Assert.Contains(expected, result.Failure!.Message, StringComparison.Ordinal);
		Assert.False(File.Exists(Options.AccountsPath));
	}

	[Fact]
	public void Замена_ключа_сессий_меняет_ключ()
	{
		ManageCommands.Initialise(Options, TextWriter.Null);
		var before = File.ReadAllBytes(Options.SecretKeyPath);

		Assert.True(ManageCommands.RotateSecret(Options, TextWriter.Null).IsSuccess);

		Assert.NotEqual(before, File.ReadAllBytes(Options.SecretKeyPath));
		Assert.Equal(48, File.ReadAllBytes(Options.SecretKeyPath).Length);
		Assert.False(File.Exists(Path.ChangeExtension(Options.SecretKeyPath, ".new")));
	}

	/// <summary>Первый сервер установщика с умолчаниями мастера: main, 42420, «Новый мир», ru.</summary>
	[Fact]
	public void Первый_сервер_получает_умолчания_мастера()
	{
		var parsed = ManageCommands.ProvisionArguments.Parse([]);

		var spec = ServerSpec.FromWizard(parsed.Value.ToAnswers("1.22.7"));

		Assert.True(spec.IsSuccess);
		Assert.Equal("main", spec.Value.Id.Value);
		Assert.Equal(ServerSpec.DefaultPort, spec.Value.Port);
		Assert.Equal(ServerSpec.DefaultWorldName, spec.Value.WorldName);
		Assert.Equal("ru", spec.Value.Language);
		Assert.Equal("1.22.7", spec.Value.Version);
		Assert.Equal(600, parsed.Value.WaitSeconds);
	}

	[Fact]
	public void Параметры_создания_разбираются()
	{
		var parsed = ManageCommands.ProvisionArguments.Parse(["--version", "1.21.6", "--port", "42500", "--id", "creative", "--wait", "900"]);

		Assert.True(parsed.IsSuccess);
		Assert.Equal("1.21.6", parsed.Value.Version);
		Assert.Equal(42500, parsed.Value.Port);
		Assert.Equal("creative", parsed.Value.Id);
		Assert.Equal(900, parsed.Value.WaitSeconds);
	}

	[Theory]
	[InlineData("--port", "много")]
	[InlineData("--wait", "0")]
	[InlineData("--unknown", "x")]
	[InlineData("--version")]
	public void Неверные_параметры_создания_отвергаются(params string[] arguments)
	{
		Assert.False(ManageCommands.ProvisionArguments.Parse(arguments).IsSuccess);
	}

	[Fact]
	public async Task Неизвестная_команда_печатает_справку()
	{
		var error = new StringWriter();

		var code = await ManageCommands.RunAsync(["frobnicate"], Options, TextWriter.Null, error);

		Assert.Equal(2, code);
		Assert.Contains("claim-token", error.ToString(), StringComparison.Ordinal);
	}
}
