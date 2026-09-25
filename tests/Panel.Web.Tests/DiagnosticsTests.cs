using System.Text.Json.Nodes;
using Panel.Contracts.Privacy;
using Panel.Observability.Domain;
using Panel.Web.Application;

namespace Panel.Web.Tests;

/// <summary>
/// Диагностический пакет: что из него удаляется и в каком виде приходят причины.
/// </summary>
public sealed class DiagnosticsTests
{
	private static IReadOnlyList<LogEvent> Journal(params string[] lines) =>
		[.. lines.Select(line => LogEvent.FromLine(line))];

	[Theory]
	[InlineData("ip")]
	[InlineData("IP")]
	[InlineData("token")]
	[InlineData("Password")]
	[InlineData("secret")]
	[InlineData("chat_id")]
	[InlineData("uid")]
	[InlineData("client_id")]
	public void Значение_чувствительного_ключа_заменяется_целиком(string key)
	{
		var payload = new JsonObject { ["notifications"] = new JsonObject { [key] = new JsonObject { ["nested"] = 1 } } };

		var redacted = DiagnosticsRedaction.Redact(payload)!;

		Assert.Equal(DiagnosticsRedaction.Redacted, (string?) redacted["notifications"]![key]);
	}

	[Fact]
	public void Адреса_в_строках_маскируются_на_любой_глубине()
	{
		var payload = new JsonObject
		{
			["recent_events"] = new JsonArray(new JsonObject { ["detail"] = "Client 10.1.2.3 disconnected" }),
			["port"] = 42420,
			["active"] = true,
			["missing"] = null,
		};

		var redacted = DiagnosticsRedaction.Redact(payload)!;

		Assert.Equal($"Client {AddressMask.Hidden} disconnected", (string?) redacted["recent_events"]![0]!["detail"]);
		Assert.Equal(42420, (int) redacted["port"]!);
		Assert.True((bool) redacted["active"]!);
		Assert.Null(redacted["missing"]);
	}

	[Fact]
	public void Исходный_пакет_не_меняется()
	{
		var payload = new JsonObject { ["ip"] = "10.1.2.3", ["line"] = "from 10.1.2.3" };

		_ = DiagnosticsRedaction.Redact(payload);

		Assert.Equal("10.1.2.3", (string?) payload["ip"]);
		Assert.Equal("from 10.1.2.3", (string?) payload["line"]);
	}

	[Fact]
	public void Готовый_сервер_без_улики_и_времени()
	{
		var watchdog = new JsonObject { ["state"] = "ready", ["detail"] = "Телеметрия свежая" };

		var causes = DiagnosticsHandler.Causes(true, watchdog, Journal("Address already in use"), string.Empty);

		var startup = causes["startup"]!.AsObject();
		Assert.Equal("ok", (string?) startup["level"]);
		Assert.Equal("ready", (string?) startup["code"]);
		Assert.False(startup.ContainsKey("evidence"));
		Assert.False(startup.ContainsKey("timestamp"));
		Assert.False(causes.ContainsKey("journal_error"));
		Assert.Empty(causes["connections"]!.AsArray());
	}

	[Fact]
	public void Найденная_причина_несёт_строку_и_время()
	{
		var causes = DiagnosticsHandler.Causes(false, new JsonObject(), Journal("Failed to bind: address already in use"), string.Empty);

		var startup = causes["startup"]!.AsObject();
		Assert.Equal("error", (string?) startup["level"]);
		Assert.Equal("port-busy", (string?) startup["code"]);
		Assert.Equal("Failed to bind: address already in use", (string?) startup["evidence"]);
		Assert.True(startup.ContainsKey("timestamp"));
	}

	[Fact]
	public void Пояснение_наблюдателя_объясняет_неготовность()
	{
		var watchdog = new JsonObject { ["state"] = "starting", ["detail"] = "Ждём первую телеметрию" };

		var causes = DiagnosticsHandler.Causes(true, watchdog, Journal("Saving"), string.Empty);

		Assert.Equal("not-ready", (string?) causes["startup"]!["code"]);
		Assert.Equal("Ждём первую телеметрию", (string?) causes["startup"]!["detail"]);
	}

	/// <summary>Пакет собирается и при недоступном журнале: это одна из возможных причин проблем.</summary>
	[Fact]
	public void Недоступный_журнал_попадает_в_причины()
	{
		var causes = DiagnosticsHandler.Causes(false, new JsonObject(), [], "journalctl не ответил");

		Assert.Equal("journalctl не ответил", (string?) causes["journal_error"]);
		Assert.Equal("stopped", (string?) causes["startup"]!["code"]);
	}

	[Fact]
	public void Отказы_в_подключении_перечисляются()
	{
		var causes = DiagnosticsHandler.Causes(
			true,
			new JsonObject { ["state"] = "ready" },
			Journal("Player Bob is not on the whitelist", "Disconnecting client Eve because server is full"),
			string.Empty);

		var codes = causes["connections"]!.AsArray().Select(item => (string?) item!["code"]).ToList();
		Assert.Equal(["full", "whitelist"], codes);
	}
}
