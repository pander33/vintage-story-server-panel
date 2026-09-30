using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Contracts.Http;
using Panel.Contracts.Privacy;
using Panel.Configuration.Ports;
using Panel.Contracts.ServerCommands;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.Observability.Domain;
using Panel.Observability.Infrastructure;
using Panel.Observability.Ports;
using Panel.Provisioning.Ports;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Middleware;

namespace Panel.Web.Endpoints;

/// <summary>
/// Журнал сервера и панели, диагностика, лента игровых событий и действия над игроками.
/// </summary>
/// <remarks>
/// Адреса скрываются после разбора: классификация и события игроков работают с исходной
/// строкой, а наблюдатель получает уже очищенную.
/// </remarks>
public static class ObservabilityEndpoints
{
	public static void MapObservability(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/logs", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IServerRuntime runtime,
			CancellationToken ct,
			int lines = 300,
			string category = "all",
			string search = "") =>
		{
			var events = await EventsAsync(http, authorizer, runtime, lines, category, search, ct).ConfigureAwait(false);
			if (!events.IsSuccess)
			{
				return PanelResults.Refuse(events.Failure!);
			}

			var crashes = CrashSummary.Of(events.Value);
			return PanelResults.Json(new LogsResponse
			{
				Lines = [.. events.Value.Select(item => item.Line)],
				Events = [.. events.Value.Select(Event)],
				Crashes = new CrashSummaryResponse
				{
					Count = crashes.Count,
					Repeated = crashes.Repeated,
					Last = crashes.Last,
				},
			});
		});

		routes.MapGet("/api/logs/download", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IServerRuntime runtime,
			TimeProvider clock,
			CancellationToken ct,
			int lines = 2000,
			string category = "all",
			string search = "") =>
		{
			var events = await EventsAsync(http, authorizer, runtime, lines, category, search, ct).ConfigureAwait(false);
			if (!events.IsSuccess)
			{
				return PanelResults.Refuse(events.Failure!);
			}

			// Имя по местному времени панели.
			var stamp = clock.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
			http.Response.Headers.ContentDisposition = $"attachment; filename=\"vintagestory-{stamp}.log\"";

			var text = string.Join("\n", events.Value.Select(item => item.Line)) + "\n";
			return Results.Text(text, "text/plain; charset=utf-8", Encoding.UTF8);
		});

		routes.MapGet("/api/panel/logs", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IProvisioningHost host,
			CancellationToken ct,
			int lines = 120) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadPanelLogs), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			// Журнал службы панели один на все серверы и к выбранному не относится.
			var journal = await host.PanelLogsAsync(Math.Clamp(lines, 20, 2000), ct).ConfigureAwait(false);
			if (!journal.IsSuccess)
			{
				return PanelResults.Refuse(journal.Failure!);
			}

			var hide = allowed.Value.HidesAddresses;
			return PanelResults.Json(journal.Value with
			{
				Lines = [.. journal.Value.Lines.Select(line => AddressMask.Scrub(line, hide))],
				Unit = journal.Value.Unit.Length > 0 ? journal.Value.Unit : "vs-panel.service",
			});
		});

		routes.MapGet("/api/host", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			HostProbe probe,
			IRegistryStore registry,
			PanelOptions options,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadTelemetry), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			// Машина одна на все серверы и к выбранному не относится.
			var memory = HostProbe.Memory();
			var load = HostProbe.Load();
			var servers = await registry.LoadAsync(ct).ConfigureAwait(false) is RegistrySnapshot.Available available
				? available.Servers
				: [];

			return PanelResults.Json(new HostResponse
			{
				Hostname = Environment.MachineName,
				Platform = probe.Platform,
				Uptime = Environment.TickCount64 / 1000,
				Cpus = Environment.ProcessorCount,
				CpuPercent = await probe.CpuPercentAsync(ct).ConfigureAwait(false),
				Load = load is { } average ? [average.One, average.Five, average.Fifteen] : null,
				MemoryTotal = memory?.Total,
				MemoryAvailable = memory?.Available,
				SwapTotal = memory?.SwapTotal,
				SwapUsed = memory?.SwapUsed,
				PanelMemory = Environment.WorkingSet,
				Volumes = HostVolumes(options, servers),
			});
		});

		routes.MapGet("/api/diagnostics", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			DiagnosticsHandler diagnostics,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadDiagnostics), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var collected = await diagnostics.CollectAsync(Instance(http), ct).ConfigureAwait(false);
			if (!collected.IsSuccess)
			{
				return PanelResults.Refuse(collected.Failure!);
			}

			// Сюда же обращаются ссылка «Скачать JSON» и опрос обзора: заголовок заставляет браузер
			// сохранить файл, а fetch его не учитывает.
			http.Response.Headers.ContentDisposition = "attachment; filename=vs-panel-diagnostics.json";
			return PanelResults.Json(collected.Value);
		});

		routes.MapGet("/api/game-events", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IGameEventFeed feed,
			CancellationToken ct,
			int lines = 200,
			string kind = "all",
			string search = "") =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadGameEvents), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var filter = GameEventFilter.Create(kind, search);
			if (!filter.IsSuccess)
			{
				return PanelResults.Refuse(filter.Failure!);
			}

			var page = await feed.ReadAsync(Instance(http), Math.Clamp(lines, 1, 500), ct).ConfigureAwait(false);
			if (!page.IsSuccess)
			{
				return PanelResults.Refuse(page.Failure!);
			}

			// Наблюдатель видит ленту, но не идентификаторы учётных записей игроков.
			var hide = allowed.Value.HidesAddresses;
			var events = filter.Value.Apply(page.Value.Events).Select(item => hide ? item.Masked() : item);

			return PanelResults.Json(new GameEventsPayload
			{
				Events = [.. events.Select(GameEventView)],
				Supported = page.Value.Supported,
			});
		});

		routes.MapGet("/api/logs/stream", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IServerRuntime runtime,
			CancellationToken ct,
			string category = "all",
			string search = "") =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadLogs), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var filter = LogFilter.Create(category, search);
			if (!filter.IsSuccess)
			{
				return PanelResults.Refuse(filter.Failure!);
			}

			// При переподключении EventSource присылает id последнего полученного события. Чтение
			// продолжается с него, а не с хвоста, иначе строки за время обрыва потеряются.
			var resumed = LogCursor.Parse(http.Request.Headers["Last-Event-ID"], PlatformLayout.Linux);
			LogCursor cursor = resumed.IsSuccess ? resumed.Value : new LogCursor.Tail();

			await StreamLogsAsync(http, runtime, filter.Value, allowed.Value.HidesAddresses, cursor, ct).ConfigureAwait(false);
			return Results.Empty;
		});

		routes.MapGet("/api/game-events/stream", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IGameEventFeed feed,
			CancellationToken ct,
			string kind = "all",
			string search = "") =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadGameEvents), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var filter = GameEventFilter.Create(kind, search);
			if (!filter.IsSuccess)
			{
				return PanelResults.Refuse(filter.Failure!);
			}

			await StreamGameEventsAsync(http, feed, filter.Value, allowed.Value.HidesAddresses, ct).ConfigureAwait(false);
			return Results.Empty;
		});

		routes.MapPost("/api/players/action", async (
			HttpContext http,
			PlayerCommandRequest body,
			CommandAuthorizer authorizer,
			IPlayerCommands players,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(SendPlayerCommand));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var command = PlayerCommand.Create(body.Action, body.Player, body.Detail);
			if (!command.IsSuccess)
			{
				return PanelResults.Refuse(command.Failure!);
			}

			var sent = await players.SendAsync(Instance(http), command.Value, ct).ConfigureAwait(false);
			if (!sent.IsSuccess)
			{
				return PanelResults.Refuse(sent.Failure!);
			}

			// Текст объявления в аудит не пишется, только сам факт.
			var detail = command.Value.IsAnnouncement
				? command.Value.Action
				: $"{command.Value.Action}:{command.Value.Player}";
			await audit.RecordAsync(http, "player_command", detail, ct).ConfigureAwait(false);

			return PanelResults.Json(sent.Value);
		});

		// Списки бана и белого списка из файлов игры (ADR-17, интерфейс 2.1 §3). Читаются и у
		// остановленного сервера: консоль для этого не нужна.
		routes.MapGet("/api/players/lists", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IPlayerListStore lists,
			IServerConfigStore config,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadPlayerLists), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var instance = Instance(http);
			var read = await lists.ReadAsync(instance, ct).ConfigureAwait(false);
			if (!read.IsSuccess)
			{
				return PanelResults.Refuse(read.Failure!);
			}

			// Режим белого списка — подсказка к списку, а не условие ответа: без настроек списки
			// всё равно показываются.
			var settings = await config.ReadAsync(instance, ct).ConfigureAwait(false);
			return PanelResults.Json(PlayerListsView.Compose(
				read.Value,
				settings.IsSuccess ? settings.Value.Config : null,
				allowed.Value.HidesAddresses));
		});
	}

	/// <summary>
	/// Живой журнал: порция после курсора раз в полторы секунды. Строка события — местная метка
	/// ISO и сообщение, без префикса узла и процесса.
	/// </summary>
	private static async Task StreamLogsAsync(
		HttpContext http,
		IServerRuntime runtime,
		LogFilter filter,
		bool hide,
		LogCursor cursor,
		CancellationToken ct)
	{
		ServerSentEvents.Begin(http);
		var instance = Instance(http);
		var quiet = 0;

		try
		{
			while (!ct.IsCancellationRequested)
			{
				var page = await runtime.LogsFromAsync(instance, cursor, ct).ConfigureAwait(false);
				if (!page.IsSuccess)
				{
					await ServerSentEvents.SendAsync(http, StreamError(http, page.Failure!.Text), ct).ConfigureAwait(false);
					await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
				}
				else
				{
					cursor = page.Value.Next;
					var id = cursor.Serialize();
					var events = filter.Apply(page.Value.Entries.Select(entry => LogEvent.FromLine(Stamped(entry), id)));

					foreach (var item in events)
					{
						var shown = hide ? item.WithLine(line => AddressMask.Scrub(line, hide: true)) : item;
						var json = LocalizedJson.Serialize(Event(shown), http);
						await ServerSentEvents.SendAsync(http, $"id: {id}\ndata: {json}\n\n", ct).ConfigureAwait(false);
					}

					quiet = events.Count > 0 ? 0 : quiet + 1;
					if (quiet >= 10)
					{
						await ServerSentEvents.SendAsync(http, $"id: {id}\n{ServerSentEvents.KeepAlive}", ct).ConfigureAwait(false);
						quiet = 0;
					}
				}

				await Task.Delay(TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			// Клиент отключился.
		}
	}

	/// <summary>
	/// Живая лента событий игры. Файл только дописывается, поэтому поток отдаёт хвост и запоминает
	/// ключи отправленных событий (<c>seq</c> обнуляется при перезапуске игры). Первая порция не
	/// отправляется: страница уже получила её через REST.
	/// </summary>
	private static async Task StreamGameEventsAsync(
		HttpContext http,
		IGameEventFeed feed,
		GameEventFilter filter,
		bool hide,
		CancellationToken ct)
	{
		const int Remembered = 1000;

		ServerSentEvents.Begin(http);
		var instance = Instance(http);
		var order = new Queue<string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var first = true;

		try
		{
			while (!ct.IsCancellationRequested)
			{
				var page = await feed.ReadAsync(instance, 200, ct).ConfigureAwait(false);
				if (!page.IsSuccess)
				{
					await ServerSentEvents.SendAsync(http, StreamError(http, page.Failure!.Text), ct).ConfigureAwait(false);
					await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
				}
				else
				{
					var fresh = new List<GameEvent>();
					foreach (var item in filter.Apply(page.Value.Events).Select(item => hide ? item.Masked() : item))
					{
						if (!seen.Add(item.Key))
						{
							continue;
						}

						order.Enqueue(item.Key);
						if (order.Count > Remembered)
						{
							seen.Remove(order.Dequeue());
						}

						fresh.Add(item);
					}

					if (first)
					{
						first = false;
						fresh.Clear();
					}

					foreach (var item in fresh)
					{
						var json = LocalizedJson.Serialize(GameEventView(item), http);
						await ServerSentEvents.SendAsync(http, ServerSentEvents.Data(json), ct).ConfigureAwait(false);
					}

					if (fresh.Count == 0)
					{
						await ServerSentEvents.SendAsync(http, ServerSentEvents.KeepAlive, ct).ConfigureAwait(false);
					}
				}

				await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			// Клиент отключился.
		}
	}

	private static string StreamError(HttpContext http, Phrase message) =>
		ServerSentEvents.Data(LocalizedJson.Serialize(new JsonObject { ["stream_error"] = JsonSerializer.SerializeToNode(message, PanelJson.Options) }, http));

	/// <summary>Местная метка ISO с двоеточием в смещении и сообщение: формат строки потока.</summary>
	private static string Stamped(LogEntry entry) =>
		entry.At.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) + " " + entry.Message;

	private static GameEventPayload GameEventView(GameEvent item) => new()
	{
		At = item.At,
		Seq = item.Seq,
		Kind = item.Kind,
		Player = item.Player,
		Uid = item.Uid,
		Channel = item.Channel,
		Text = item.Text,
		Detail = item.Detail,
	};

	/// <summary>
	/// Общая часть журнала и его выгрузки: права, фильтр, чтение, разбор, фильтрация, сокрытие
	/// адресов. Фильтр проверяется до обращения к демону, чтобы неверный запрос не стоил вызова
	/// journalctl.
	/// </summary>
	private static async Task<Result<IReadOnlyList<LogEvent>>> EventsAsync(
		HttpContext http,
		CommandAuthorizer authorizer,
		IServerRuntime runtime,
		int lines,
		string category,
		string search,
		CancellationToken ct)
	{
		var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadLogs), requireCsrf: false);
		if (!allowed.IsSuccess)
		{
			return Result.Fail<IReadOnlyList<LogEvent>>(allowed.Failure!);
		}

		var filter = LogFilter.Create(category, search);
		if (!filter.IsSuccess)
		{
			return Result.Fail<IReadOnlyList<LogEvent>>(filter.Failure!);
		}

		var entries = await runtime.LogsAsync(Instance(http), Math.Clamp(lines, 20, 2000), ct).ConfigureAwait(false);
		if (!entries.IsSuccess)
		{
			return Result.Fail<IReadOnlyList<LogEvent>>(entries.Failure!);
		}

		var hide = allowed.Value.HidesAddresses;
		var events = filter.Value.Apply(entries.Value.Select(entry => LogEvent.FromLine(entry.Line)));

		return hide
			? Result.Ok<IReadOnlyList<LogEvent>>([.. events.Select(item => item.WithLine(line => AddressMask.Scrub(line, hide: true)))])
			: Result.Ok(events);
	}

	internal static LogEventResponse Event(LogEvent item) => new()
	{
		Line = item.Line,
		Category = LogCategoryWire.ToWire(item.Category),
		PlayerEvent = item.PlayerChange is { } change
			? new PlayerChangeResponse { Type = change.Type, Player = change.Player }
			: null,
		Overload = item.Overload,
		Cursor = item.Cursor,
		Timestamp = item.Timestamp,
	};

	/// <summary>
	/// Тома, на которых лежат состояние панели, данные и копии серверов, каждый один раз. Все тома
	/// машины не перечисляются: на Linux это ещё и <c>/proc</c>, <c>tmpfs</c> и образы snap.
	/// </summary>
	private static List<HostVolumeResponse> HostVolumes(PanelOptions options, IReadOnlyList<RegisteredServer> servers)
	{
		IReadOnlyList<DriveInfo> drives;
		try
		{
			drives = DriveInfo.GetDrives();
		}
		catch (IOException)
		{
			return [];
		}

		var places = new List<(string Path, HostVolumeUseResponse Use)> { (options.StateDirectory, new HostVolumeUseResponse { Kind = "panel" }) };
		foreach (var server in servers)
		{
			places.Add((server.DataDirectory, new HostVolumeUseResponse { Kind = "data", Server = server.Name }));
			places.Add((server.BackupDirectory, new HostVolumeUseResponse { Kind = "backups", Server = server.Name }));
		}

		var volumes = new List<HostVolumeResponse>();
		foreach (var (path, use) in places)
		{
			if (HostProbe.VolumeOf(path, drives) is not { } space)
			{
				continue;
			}

			var index = volumes.FindIndex(volume => volume.Mount == space.Mount);
			if (index < 0)
			{
				volumes.Add(new HostVolumeResponse { Mount = space.Mount, Total = space.Total, Free = space.Free, Uses = [use] });
			}
			else if (!volumes[index].Uses.Contains(use))
			{
				volumes[index] = volumes[index] with { Uses = [.. volumes[index].Uses, use] };
			}
		}

		return volumes;
	}

	private static InstanceId Instance(HttpContext http) => PanelContext.Get(http)!.Instance;
}
