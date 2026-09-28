using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Panel.Access.Domain;
using Panel.Access.Infrastructure;
using Panel.Access.Passwords;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;
using Panel.Web.Application;

namespace Panel.Web.Tests;

/// <summary>
/// Состояние мастера на диске: принятие уже настроенной панели, запись ответа до ответа
/// клиенту, одноразовый токен.
/// </summary>
public sealed class SetupServiceTests : IDisposable
{
	private readonly string state = Path.Combine(Path.GetTempPath(), "vs-setup-" + Guid.NewGuid().ToString("n"));
	private readonly PanelOptions options;
	private readonly FileAccountStore accounts;
	private readonly FakeRegistry registry = new();

	public SetupServiceTests()
	{
		Directory.CreateDirectory(state);
		options = new PanelOptions { StateDirectory = state, DefaultInstance = "main" };
		accounts = new FileAccountStore(options.AccountsPath, options.LegacyPasswordPath);
	}

	public void Dispose()
	{
		if (Directory.Exists(state))
		{
			Directory.Delete(state, recursive: true);
		}
	}

	private SetupService Service() =>
		new(options, accounts, registry, TimeProvider.System, NullLogger<SetupService>.Instance);

	private async Task AddAdministratorAsync()
	{
		var directory = new AccountDirectory([]).Put(UserName.Administrator, PasswordHash.Create("correct horse battery").Value, Role.Admin).Value;
		Assert.True((await accounts.SaveAsync(directory, CancellationToken.None)).IsSuccess);
	}

	private JsonObject SavedState() => JsonNode.Parse(File.ReadAllText(options.SetupStatePath))!.AsObject();

	[Fact]
	public async Task Настроенная_панель_закрывает_мастер_при_старте()
	{
		await AddAdministratorAsync();
		registry.Servers.Add(InstanceId.Parse("main"));

		await Service().StartAsync(CancellationToken.None);

		var saved = SavedState();
		Assert.True((bool) saved["completed"]!);
		Assert.Equal("main", (string?) saved["instance"]);
	}

	[Fact]
	public async Task Без_администратора_мастер_остаётся_открытым()
	{
		registry.Servers.Add(InstanceId.Parse("main"));

		var service = Service();
		await service.StartAsync(CancellationToken.None);

		Assert.False((await service.CurrentAsync(CancellationToken.None)).IsCompleted);
	}

	[Fact]
	public async Task Нечитаемый_реестр_оставляет_мастер_открытым()
	{
		await AddAdministratorAsync();
		registry.Unavailable = true;

		var service = Service();
		await service.StartAsync(CancellationToken.None);

		Assert.False((await service.CurrentAsync(CancellationToken.None)).IsCompleted);
		Assert.False(File.Exists(options.SetupStatePath));
	}

	[Fact]
	public async Task Ответ_записан_на_диск_до_ответа_и_переживает_перезапуск()
	{
		var service = Service();
		await service.ChangeAsync(journey => journey.Claim(new HashSet<InstanceId>(), 100), CancellationToken.None);
		var access = AccessSettings.Create("0.0.0.0", 9000, "panel.example", secureCookie: true).Value;

		var remembered = await service.ChangeAsync(journey => journey.RememberAccess(access), CancellationToken.None);

		Assert.True(remembered.IsSuccess);
		var saved = SavedState();
		Assert.Equal("preflight", (string?) saved["step"]);
		Assert.Equal("9000", (string?) saved["draft"]!["access"]!["PANEL_PORT"]);
		Assert.True((bool) saved["draft"]!["access"]!["published"]!);

		var restarted = await Service().CurrentAsync(CancellationToken.None);
		Assert.Equal(SetupStep.Preflight, restarted.Step);
		if (!OperatingSystem.IsWindows())
		{
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(options.SetupStatePath));
		}
	}

	[Fact]
	public async Task Отказ_агрегата_файл_не_трогает()
	{
		var service = Service();

		var refused = await service.ChangeAsync(journey => journey.RememberMachineCheck(), CancellationToken.None);

		Assert.False(refused.IsSuccess);
		Assert.False(File.Exists(options.SetupStatePath));
	}

	[Fact]
	public void Токен_читается_и_тратится_один_раз()
	{
		var service = Service();
		Assert.Null(service.ReadToken());

		File.WriteAllText(options.SetupTokenPath, "one-time\n");
		Assert.True(service.ReadToken()!.Accepts(OfferedToken.Create("one-time")));

		service.ConsumeToken();
		Assert.Null(service.ReadToken());
		Assert.False(File.Exists(options.SetupTokenPath));
	}

	[Fact]
	public void Попытки_захвата_ограничены_восемью_за_пять_минут()
	{
		var service = Service();
		for (var attempt = 0; attempt < LoginThrottle.MaxAttempts; attempt++)
		{
			Assert.False(service.ClaimThrottle.IsBlocked(SetupService.ClaimKey));
			service.ClaimThrottle.RecordFailure(SetupService.ClaimKey);
		}

		Assert.True(service.ClaimThrottle.IsBlocked(SetupService.ClaimKey));
	}

	private sealed class FakeRegistry : IRegistryStore
	{
		public List<InstanceId> Servers { get; } = [];

		public bool Unavailable { get; set; }

		public Task<RegistrySnapshot> LoadAsync(CancellationToken ct) =>
			Task.FromResult<RegistrySnapshot>(Unavailable
				? new RegistrySnapshot.Unavailable(Phrase.From("нет доступа"))
				: new RegistrySnapshot.Available(
					[.. Servers.Select(id => new RegisteredServer { Id = id, Name = id.Value, Unit = $"vs-{id.Value}.service" })],
					null,
					RegistryStamp.Absent));
	}
}
