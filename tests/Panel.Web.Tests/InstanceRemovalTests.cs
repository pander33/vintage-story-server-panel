using Microsoft.Extensions.Logging.Abstractions;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.Maintenance.Domain;
using Panel.Observability.Infrastructure;
using Panel.Provisioning.Domain;
using Panel.Provisioning.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;

namespace Panel.Web.Tests;

/// <summary>
/// Удаление сервера на стороне панели: что останавливает запрос до демона и что панель удаляет
/// после удаления сервера.
/// </summary>
public sealed class InstanceRemovalTests : IDisposable
{
	private static readonly InstanceId second = InstanceId.Parse("second");

	private readonly string state = Path.Combine(Path.GetTempPath(), "vs-removal-" + Guid.NewGuid().ToString("n"));
	private readonly FakeRegistry registry = new();
	private readonly FakeHost host = new();
	private readonly MaintenanceCoordinator coordinator = new();
	private readonly PanelOptions options;
	private readonly ServerMonitors monitors;
	private readonly PanelInstance panelInstance;
	private readonly InstanceRemoval removal;

	public InstanceRemovalTests()
	{
		options = new PanelOptions { StateDirectory = state, DefaultInstance = "main" };
		var events = new EventBus(
			options,
			TimeProvider.System,
			new NotificationSettingsFile(Path.Combine(state, "notifications.json")),
			NullLogger<EventBus>.Instance);
		monitors = new ServerMonitors(options, TimeProvider.System, events);
		panelInstance = new PanelInstance(options);
		removal = new InstanceRemoval(registry, host, coordinator, monitors, panelInstance, options);
	}

	public void Dispose()
	{
		if (Directory.Exists(state))
		{
			Directory.Delete(state, recursive: true);
		}
	}

	private string History(InstanceId instance)
	{
		var directory = options.InstanceDirectory(instance);
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "metrics.jsonl"), "{}\n");
		return directory;
	}

	[Fact]
	public async Task Удалённый_сервер_уносит_свою_историю_и_монитор()
	{
		var history = History(second);
		var kept = History(InstanceId.Parse("main"));
		var before = await monitors.GetAsync(second);

		var deleted = await removal.DeleteAsync(second, "УДАЛИТЬ second", CancellationToken.None);

		Assert.True(deleted.IsSuccess);
		Assert.Equal(["second"], registry.Deleted);
		Assert.False(Directory.Exists(history));
		Assert.True(Directory.Exists(kept));
		Assert.NotSame(before, await monitors.GetAsync(second));
	}

	[Theory]
	[InlineData("")]
	[InlineData("УДАЛИТЬ main")]
	[InlineData("ЗАБЫТЬ second")]
	[InlineData("удалить second")]
	public async Task Чужая_фраза_не_доходит_до_демона(string phrase)
	{
		var history = History(second);

		var deleted = await removal.DeleteAsync(second, phrase, CancellationToken.None);

		Assert.Equal(FailureCode.Invalid, deleted.Failure!.Code);
		Assert.Equal("Фраза подтверждения не совпадает", deleted.Failure.Message);
		Assert.Empty(registry.Deleted);
		Assert.True(Directory.Exists(history));
	}

	/// <summary>
	/// Сервер PANEL_INSTANCE удаляется, но сначала панель переходит на умолчание реестра: к
	/// PANEL_INSTANCE она обращается и без реестра.
	/// </summary>
	[Fact]
	public async Task Удаление_сервера_по_умолчанию_переводит_панель_на_другой()
	{
		registry.Default = "second";

		var deleted = await removal.DeleteAsync(InstanceId.Parse("main"), "УДАЛИТЬ main", CancellationToken.None);

		Assert.True(deleted.IsSuccess);
		Assert.Equal(["PANEL_INSTANCE=second"], host.Written);
		Assert.Equal("second", panelInstance.Value);
		Assert.Equal(["main"], registry.Deleted);
	}

	[Fact]
	public async Task Без_умолчания_реестра_панель_переходит_на_первый_по_имени()
	{
		registry.Ids = ["main", "zeta", "alpha"];
		registry.Default = "main";

		var moved = await removal.LeaveAsync(InstanceId.Parse("main"), CancellationToken.None);

		Assert.True(moved.IsSuccess);
		Assert.Equal("alpha", panelInstance.Value);
	}

	[Fact]
	public async Task Другой_сервер_удаляется_без_смены_умолчания()
	{
		await removal.DeleteAsync(second, "УДАЛИТЬ second", CancellationToken.None);

		Assert.Empty(host.Written);
		Assert.Equal("main", panelInstance.Value);
	}

	[Fact]
	public async Task Единственный_сервер_не_удаляется()
	{
		registry.Ids = ["main"];

		var deleted = await removal.DeleteAsync(InstanceId.Parse("main"), "УДАЛИТЬ main", CancellationToken.None);

		Assert.Equal(FailureCode.Conflict, deleted.Failure!.Code);
		Assert.Empty(host.Written);
		Assert.Empty(registry.Deleted);
	}

	/// <summary>Незаписанный vs-panel.env останавливает удаление: панель осталась бы без сервера.</summary>
	[Fact]
	public async Task Незаписанное_умолчание_останавливает_удаление()
	{
		host.Refusal = Failure.Unavailable("vs-panel.env is read-only");

		var deleted = await removal.DeleteAsync(InstanceId.Parse("main"), "УДАЛИТЬ main", CancellationToken.None);

		Assert.Equal(FailureCode.Unavailable, deleted.Failure!.Code);
		Assert.Equal("main", panelInstance.Value);
		Assert.Empty(registry.Deleted);
	}

	[Fact]
	public async Task Сервер_на_обслуживании_не_удаляется()
	{
		using var held = coordinator.TryAcquire(second, LeasePurpose.ScheduledMaintenance);

		var deleted = await removal.DeleteAsync(second, "УДАЛИТЬ second", CancellationToken.None);

		Assert.NotNull(held);
		Assert.Equal(FailureCode.Conflict, deleted.Failure!.Code);
		Assert.Equal(InstanceRemoval.BusyRefusal, deleted.Failure.Text);
		Assert.Empty(registry.Deleted);
	}

	/// <summary>Если сервер остался в реестре, его история тоже остаётся.</summary>
	[Fact]
	public async Task Отказ_демона_оставляет_историю()
	{
		var history = History(second);
		registry.Refusal = Failure.Conflict("This instance was not created by the panel; its files are managed elsewhere");

		var deleted = await removal.DeleteAsync(second, "УДАЛИТЬ second", CancellationToken.None);

		Assert.Equal(FailureCode.Conflict, deleted.Failure!.Code);
		Assert.True(Directory.Exists(history));
	}

	private sealed class FakeRegistry : IRegistryAdministration
	{
		public List<string> Deleted { get; } = [];

		public Failure? Refusal { get; set; }

		public IReadOnlyList<string> Ids { get; set; } = ["main", "second"];

		public string? Default { get; set; }

		public Task<Result<RegistryListingPayload>> ListAsync(CancellationToken ct) =>
			Task.FromResult(Result.Ok(new RegistryListingPayload
			{
				Instances = [.. Ids.Select(id => new RegistryEntryPayload { Id = id })],
				Default = Default,
				Writable = true,
			}));

		public Task<Result<InstanceRenamedPayload>> RenameAsync(InstanceId instance, InstanceName name, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<Result<InstanceForgottenPayload>> ForgetAsync(InstanceId instance, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<Result<InstanceDeletedPayload>> DeleteAsync(InstanceId instance, CancellationToken ct)
		{
			if (Refusal is { } refusal)
			{
				return Task.FromResult(Result.Fail<InstanceDeletedPayload>(refusal));
			}

			Deleted.Add(instance.Value);
			return Task.FromResult(Result.Ok(new InstanceDeletedPayload
			{
				Id = instance.Value,
				Root = "/srv/vs-panel/" + instance.Value,
				Unit = $"vs-{instance.Value}.service",
				Default = "main",
			}));
		}
	}

	private sealed class FakeHost : IProvisioningHost
	{
		public List<string> Written { get; } = [];

		public Failure? Refusal { get; set; }

		public Task<Result<PanelEnvironmentSetPayload>> SetPanelEnvironmentAsync(PanelSetting setting, CancellationToken ct)
		{
			if (Refusal is { } refusal)
			{
				return Task.FromResult(Result.Fail<PanelEnvironmentSetPayload>(refusal));
			}

			Written.Add($"{setting.Key}={setting.Value}");
			return Task.FromResult(Result.Ok(new PanelEnvironmentSetPayload { Key = setting.Key }));
		}

		public Task<Result<RuntimeProbePayload>> RuntimeProbeAsync(CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<DiskFreePayload>> DiskFreeAsync(CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<InstanceCreatedPayload>> CreateInstanceAsync(ServerSpec spec, StagedFile package, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<Result<TransferInspectionPayload>> InspectTransferAsync(StagedFile transfer, string sha256, long length, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<Result<InstanceCreatedPayload>> ImportInstanceAsync(ServerSpec spec, StagedFile gamePackage, StagedFile transfer, string sha256, long length, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<Result<ServiceInstalledPayload>> InstallServiceAsync(InstanceId instance, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<Result<PanelRestartPayload>> RestartPanelAsync(CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<InstancesRootSetPayload>> SetInstancesRootAsync(string path, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<Result<PanelLogsPayload>> PanelLogsAsync(int lines, CancellationToken ct) => throw new NotSupportedException();
	}
}
