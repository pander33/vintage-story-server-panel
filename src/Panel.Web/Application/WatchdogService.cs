using System.Text.Json.Nodes;
using Panel.Maintenance.Domain;
using Panel.Observability.Domain;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>Суждение о готовности и разрешённое вердиктом автоматическое восстановление.</summary>
internal sealed class WatchdogService(
	AutomationWorkspaceStore workspaces, IServerRuntime runtime, ITelemetryReader telemetry,
	ReadinessPolicy policy, ReadinessLog readiness, ServerControlHandler control,
	EventBus events, MaintenanceCoordinator coordinator, AutomationRunJournal journal, TimeProvider clock)
{
	private static readonly TimeSpan readinessPoll = TimeSpan.FromSeconds(5);

	/// <summary>Готовность и восстановление вычисляет только наблюдатель.</summary>
	internal async Task<JsonObject> TickAsync(InstanceId instance, CancellationToken ct)
	{
		var workspace = await workspaces.GetAsync(instance).ConfigureAwait(false);
		await workspace.WatchdogGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			var now = clock.GetUtcNow();
			string previousState;
			lock (workspace.Sync)
			{
				previousState = TelemetryNumbers.Text(workspace.Health["state"]);
			}

			var status = await runtime.StatusAsync(instance, ct).ConfigureAwait(false);
			if (!status.IsSuccess)
			{
				lock (workspace.Sync)
				{
					workspace.Health = new JsonObject
					{
						["state"] = "unknown",
						["detail"] = PhraseJsonConverter.ToNode(status.Failure!.Text),
						["checked_at"] = now.ToUnixTimeSeconds(),
						["failures"] = 0,
					};
					workspace.Chain = null;
				}
			}
			else
			{
				var reading = await telemetry.ReadAsync(instance, ct).ConfigureAwait(false);
				bool recover;
				lock (workspace.Sync)
				{
					var verdict = policy.Evaluate(status.Value, reading, workspace.Watchdog, workspace.Chain, now);
					workspace.Chain = verdict;
					workspace.Health = HealthJson(verdict);
					recover = verdict.AuthorizesAutomaticRecovery && workspace.Watchdog.AutoRestart;
				}

				if (recover)
				{
					await RecoverAsync(instance, workspace, now, ct).ConfigureAwait(false);
				}
			}

			JsonObject health;
			lock (workspace.Sync)
			{
				workspace.Restarts = [.. RecoveryBudget.Recent(workspace.Restarts, now)];
				readiness.Record(instance, workspace.Chain, RecoveryBlocked(workspace.Health));
				health = (JsonObject) workspace.Health.DeepClone();
			}
			await workspace.SaveRequiredAsync(ct).ConfigureAwait(false);

			var state = TelemetryNumbers.Text(health["state"]);
			if (state != previousState && state is "ready" or "hung" or "stopped")
			{
				await events.EmitAsync(
						instance,
						"watchdog." + state,
						state == "hung" ? EventSeverity.Critical : state == "stopped" ? EventSeverity.Warning : EventSeverity.Info,
						Phrase.Format($"Watchdog: {PhraseJsonConverter.FromNode(health["detail"]) ?? Phrase.From(string.Empty)}"),
						source: "watchdog",
						ct: ct)
					.ConfigureAwait(false);
			}

			return health;
		}
		finally
		{
			workspace.WatchdogGate.Release();
		}
	}

	/// <summary>Разовая проверка не продвигает цепочку и не запускает восстановление.</summary>
	public async Task<JsonObject> CheckWatchdogAsync(InstanceId instance, CancellationToken ct)
	{
		var workspace = await workspaces.GetAsync(instance).ConfigureAwait(false);
		var now = clock.GetUtcNow();
		var status = await runtime.StatusAsync(instance, ct).ConfigureAwait(false);
		if (!status.IsSuccess)
		{
			return new JsonObject
			{
				["state"] = "unknown",
				["detail"] = PhraseJsonConverter.ToNode(status.Failure!.Text),
				["checked_at"] = now.ToUnixTimeSeconds(),
				["failures"] = 0,
			};
		}
		var reading = await telemetry.ReadAsync(instance, ct).ConfigureAwait(false);
		lock (workspace.Sync)
		{
			return HealthJson(policy.Evaluate(status.Value, reading, workspace.Watchdog, workspace.Chain, now));
		}
	}

	/// <summary>Последний вердикт наблюдателя; его отдаёт <c>/api/status</c>.</summary>
	public async Task<(ReadinessVerdict? Verdict, JsonObject Health)> CurrentHealthAsync(InstanceId instance)
	{
		var workspace = await workspaces.GetAsync(instance).ConfigureAwait(false);
		lock (workspace.Sync)
		{
			return (workspace.Chain, (JsonObject) workspace.Health.DeepClone());
		}
	}

	public async Task<WatchdogSettings> WatchdogAsync(InstanceId instance)
	{
		var workspace = await workspaces.GetAsync(instance).ConfigureAwait(false);
		lock (workspace.Sync)
		{
			return workspace.Watchdog;
		}
	}

	private async Task RecoverAsync(InstanceId instance, AutomationWorkspace workspace, DateTimeOffset now, CancellationToken ct)
	{
		Phrase? blocker;
		lock (workspace.Sync)
		{
			workspace.Restarts = [.. RecoveryBudget.Recent(workspace.Restarts, now)];
			blocker = RecoveryBudget.Blocker(workspace.Restarts, workspace.Watchdog, now);
		}
		if (blocker is not null)
		{
			lock (workspace.Sync)
			{
				workspace.Health["recovery_blocked"] = PhraseJsonConverter.ToNode(blocker);
			}
			return;
		}

		using var lease = coordinator.TryAcquire(instance, LeasePurpose.WatchdogRecovery);
		if (lease is null)
		{
			lock (workspace.Sync)
			{
				workspace.Health["recovery_blocked"] = PhraseJsonConverter.ToNode(Phrase.From("Выполняется другая общая операция"));
			}
			return;
		}

		var restarted = await control.ServiceAsync(instance, ServiceAction.Restart, ct).ConfigureAwait(false);
		if (!restarted.IsSuccess)
		{
			lock (workspace.Sync)
			{
				workspace.Health["recovery_blocked"] = PhraseJsonConverter.ToNode(Phrase.Format($"Ошибка перезапуска: {restarted.Failure!}"));
			}
			return;
		}

		lock (workspace.Sync)
		{
			workspace.Restarts.Add(now);

			// После перезапуска цепочка начинается заново: у нового процесса свой счёт.
			workspace.Chain = null;
			workspace.Health = new JsonObject
			{
				["state"] = "recovering",
				["detail"] = PhraseJsonConverter.ToNode(Phrase.From("Watchdog перезапустил зависший сервер")),
				["checked_at"] = now.ToUnixTimeSeconds(),
				["failures"] = 0,
			};

		}

		var run = new MaintenanceRun(Guid.NewGuid().ToString("N"), null, "Watchdog recovery", "watchdog", now.ToUnixTimeSeconds());
		run.Recovered(Now());
		await journal.RecordAsync(instance, workspace, run, withTrigger: true, ct).ConfigureAwait(false);
	}

	public async Task<Result<JsonObject>> SetWatchdogAsync(InstanceId instance, JsonObject values, CancellationToken ct)
	{
		var settings = WatchdogSettingsJson.FromJson(values);
		if (!settings.IsSuccess)
		{
			return Result.Fail<JsonObject>(settings.Failure!);
		}

		var workspace = await workspaces.GetAsync(instance).ConfigureAwait(false);
		await workspace.WatchdogGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			WatchdogSettings previous;
			lock (workspace.Sync)
			{
				previous = workspace.Watchdog;
				workspace.Watchdog = settings.Value;
			}
			var committed = false;
			try
			{
				var written = await workspace.SaveAsync(ct).ConfigureAwait(false);
				if (!written.IsSuccess)
				{
					return written.Failure!;
				}
				readiness.Configure(instance, settings.Value);
				committed = true;
			}
			finally
			{
				if (!committed)
				{
					lock (workspace.Sync)
					{
						workspace.Watchdog = previous;
					}
				}
			}
		}
		finally
		{
			workspace.WatchdogGate.Release();
		}
		return WatchdogSettingsJson.ToJson(settings.Value);
	}

	/// <summary>
	/// Ожидание готовности. Сервер без мода телеметрии готовность подтвердить не может, поэтому
	/// «ограниченная проверка» завершает ожидание сразу, а не по таймауту.
	/// </summary>
	public async Task<bool> WaitReadyAsync(InstanceId instance, TimeSpan timeout, CancellationToken ct)
	{
		var deadline = clock.GetUtcNow() + timeout;
		while (clock.GetUtcNow() < deadline)
		{
			var health = await CheckWatchdogAsync(instance, ct).ConfigureAwait(false);
			switch (TelemetryNumbers.Text(health["state"]))
			{
				case "ready":
					return true;
				case "degraded":
					return false;
			}

			await Task.Delay(readinessPoll, ct).ConfigureAwait(false);
		}

		return false;
	}

	internal static JsonObject HealthJson(ReadinessVerdict verdict) => new()
	{
		["checked_at"] = verdict.CheckedAt.ToUnixTimeSeconds(),
		["instance"] = verdict.Observed.Serialize(),
		["active"] = verdict.ServiceActive,
		["state"] = verdict.State.ToWire(),
		["detail"] = PhraseJsonConverter.ToNode(verdict.Detail),
		["failures"] = verdict.ConsecutiveFailures,
	};

	internal static Phrase? RecoveryBlocked(JsonObject health) =>
		PhraseJsonConverter.FromNode(health["recovery_blocked"]) is { } blocked && blocked.ToString().Length > 0 ? blocked : null;

	private long Now() => clock.GetUtcNow().ToUnixTimeSeconds();
}
