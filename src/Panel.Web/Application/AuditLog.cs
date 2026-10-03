using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Panel.Contracts;
using Panel.Observability.Domain;
using Panel.SharedKernel;
using Panel.Web.Middleware;

namespace Panel.Web.Application;

/// <summary>
/// Журнал действий администратора: <c>audit.jsonl</c> в каталоге состояния панели и событие
/// <c>admin.&lt;действие&gt;</c> в ленте сервера, к которому относится запрос.
/// </summary>
/// <remarks>
/// <para>
/// Журнал общий для панели, а не для сервера: действие «удалил учётную запись» не относится ни
/// к одному игровому серверу. Запись: ts, ip, event, detail с обрезкой до 500 символов,
/// кириллица без экранирования; у запроса с удостоверением ещё user и, если запрос пришёл
/// с API-токеном, api_token — имя токена.
/// </para>
/// <para>
/// Важность события — <c>warning</c>, если в имени действия есть «failed», «restore»,
/// «rollback», «update» или «delete», иначе <c>info</c>; уведомления отправляются только для
/// предупреждений. Сервер берётся из контекста запроса явно.
/// </para>
/// <para>
/// Ошибка записи не отменяет действие, а только пишется в журнал панели. Иначе при
/// переполненном диске нельзя было бы даже остановить сервер.
/// </para>
/// </remarks>
public sealed class AuditLog
{
	private const int maxDetailLength = 500;

	private static readonly string[] alarming = ["failed", "restore", "rollback", "update", "delete"];

	private readonly JsonLinesFile file;
	private readonly TimeProvider time;
	private readonly EventBus events;

	public AuditLog(JsonLinesFile file, TimeProvider time, EventBus events)
	{
		ArgumentNullException.ThrowIfNull(file);
		ArgumentNullException.ThrowIfNull(events);

		this.file = file;
		this.time = time;
		this.events = events;
	}

	public async Task RecordAsync(HttpContext http, string action, string detail, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(http);

		var identity = PanelContext.Get(http)?.Identity;
		var record = new JsonObject
		{
			["ts"] = time.GetUtcNow().ToUnixTimeSeconds(),
			["ip"] = http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
			["event"] = action,
			["detail"] = detail.Length > maxDetailLength ? detail[..maxDetailLength] : detail,
		};

		// Кто действовал: вошедший пользователь и, если запрос пришёл с API-токеном, его имя. Вход и
		// неудачная попытка входа идут без сессии: имя тогда — в detail.
		if (identity is not null)
		{
			record["user"] = identity.User;
			if (identity.ApiToken is { } token)
			{
				record["api_token"] = token;
			}
		}

		await file.AppendAsync(record.ToJsonString(PanelJson.Options), ct).ConfigureAwait(false);

		if (PanelContext.Get(http)?.Instance is not { IsEmpty: false } instance)
		{
			return;
		}

		var severity = alarming.Any(word => action.Contains(word, StringComparison.Ordinal))
			? EventSeverity.Warning
			: EventSeverity.Info;

		await events.EmitAsync(
				instance,
				"admin." + action,
				severity,
				Phrase.Format($"Действие администратора: {action}"),
				Phrase.From(detail),
				"audit",
				notify: severity != EventSeverity.Info,
				ct: ct)
			.ConfigureAwait(false);
	}
}
