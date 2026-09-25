using System.Text.Json;
using Panel.Contracts;
using Panel.Runtime.Domain;
using Panel.Web.Application;

namespace Panel.Contracts.Tests;

/// <summary>
/// Контрактные тесты ответа GET /api/status.
/// </summary>
/// <remarks>
/// fixtures/status.expected.json составлен вручную, а не снят с работающей панели. Снимок с
/// тестового сервера кладётся в fixtures/live/.
/// </remarks>
public sealed class StatusContractTests
{
	private static readonly DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1758369600);

	[Fact]
	public void Форма_ответа_совпадает_с_панелью_1_2()
	{
		var json = JsonSerializer.Serialize(Response(), PanelJson.Options);

		JsonShape.AssertSameShape(Fixture("status.expected.json"), json);
	}

	[Fact]
	public void Кириллица_не_экранируется_в_escape_последовательности()
	{
		var json = JsonSerializer.Serialize(Response(), PanelJson.Options);

		Assert.Contains("Сервер отвечает", json, StringComparison.Ordinal);
		Assert.DoesNotContain("\\u0421", json, StringComparison.Ordinal);
	}

	[Fact]
	public void Имена_полей_остаются_в_нижнем_регистре_с_подчёркиванием()
	{
		var json = JsonSerializer.Serialize(Response(), PanelJson.Options);

		Assert.Contains("\"cpu_ns\"", json, StringComparison.Ordinal);
		Assert.Contains("\"started_at\"", json, StringComparison.Ordinal);
		Assert.DoesNotContain("\"cpuNs\"", json, StringComparison.Ordinal);
	}

	/// <summary>
	/// Состояние здоровья есть в ответе всегда и со всеми шестью ключами, даже до первой проверки.
	/// </summary>
	[Fact]
	public void Здоровье_присутствует_и_до_первой_проверки()
	{
		var response = GetServerStatusHandler.Compose(
			ServiceSnapshot.Stopped with { Unit = "vintagestory.service" },
			ReadinessVerdict.NotCheckedYet(now));

		var json = JsonSerializer.Serialize(response, PanelJson.Options);

		JsonShape.AssertSameShape(Fixture("status.expected.json"), json);
		Assert.Equal("unknown", response.Health.State);
	}

	private static Contracts.Http.StatusResponse Response()
	{
		var service = new ServiceSnapshot
		{
			IsActive = true,
			State = RuntimeState.Running,
			StateText = "active",
			SubState = "running",
			Identity = new ProcessIdentity("6f1c4c2a-0000-0000-0000-000000000001", 4242, 900),
			Pid = 4242,
			StartedAt = now - TimeSpan.FromHours(3),
			Uptime = TimeSpan.FromHours(3),
			MemoryBytes = 3221225472,
			CpuNanoseconds = 987654321,
			StartedText = "Sat 2026-09-20 09:00:00 UTC",
			Unit = "vintagestory.service",
		};

		var verdict = new ReadinessPolicy().Evaluate(
			service,
			new TelemetryReading.Fresh(new TelemetrySnapshot { GeneratedAt = now }),
			WatchdogSettings.Default,
			previous: null,
			now);

		return GetServerStatusHandler.Compose(service, verdict);
	}

	private static string Fixture(string name) => File.ReadAllText(
		Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", name));
}
