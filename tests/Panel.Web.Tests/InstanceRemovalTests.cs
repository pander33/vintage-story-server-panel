using Microsoft.Extensions.Logging.Abstractions;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.Maintenance.Domain;
using Panel.Observability.Infrastructure;
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
	private readonly MaintenanceCoordinator coordinator = new();
	private readonly PanelOptions options;
	private readonly ServerMonitors monitors;
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
		removal = new InstanceRemoval(registry, coordinator, monitors, options);
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

	[Fact]
	public async Task Сервер_PANEL_INSTANCE_не_удаляется()
	{
		var deleted = await removal.DeleteAsync(InstanceId.Parse("main"), "УДАЛИТЬ main", CancellationToken.None);

		Assert.Equal(FailureCode.Invalid, deleted.Failure!.Code);
		Assert.Equal(InstanceRemoval.PanelInstanceRefusal, deleted.Failure.Message);
		Assert.Empty(registry.Deleted);
	}

	[Fact]
	public async Task Сервер_на_обслуживании_не_удаляется()
	{
		using var held = coordinator.TryAcquire(second, LeasePurpose.ScheduledMaintenance);

		var deleted = await removal.DeleteAsync(second, "УДАЛИТЬ second", CancellationToken.None);

		Assert.NotNull(held);
		Assert.Equal(FailureCode.Conflict, deleted.Failure!.Code);
		Assert.Equal(InstanceRemoval.BusyRefusal, deleted.Failure.Message);
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

		public Task<Result<RegistryListingPayload>> ListAsync(CancellationToken ct) =>
			throw new NotSupportedException();

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
}
