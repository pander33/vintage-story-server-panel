using Panel.Agent.Client;
using Panel.Contracts.Agent;
using Panel.Runtime.Domain;

namespace Panel.Contracts.Tests;

/// <summary>
/// Перевод контракта привилегированной стороны в доменные типы.
/// </summary>
public sealed class AgentMappingTests
{
	[Fact]
	public void Идентичность_процесса_разбирается_на_три_части()
	{
		var snapshot = AgentServerRuntime.ToSnapshot(new ServiceStatusPayload
		{
			Active = true,
			State = "active",
			SubState = "running",
			Pid = 4242,
			Instance = "boot-abc:4242:900",
		});

		Assert.Equal(new ProcessIdentity("boot-abc", 4242, 900), snapshot.Identity);
		Assert.Equal("boot-abc:4242:900", snapshot.Identity.Serialize());
	}

	/// <summary>
	/// Процесс мог завершиться между systemctl show и чтением /proc. Статус всё равно приходит:
	/// без идентичности, но с pid от systemd.
	/// </summary>
	[Fact]
	public void Пустая_идентичность_не_отменяет_снимок()
	{
		var snapshot = AgentServerRuntime.ToSnapshot(new ServiceStatusPayload
		{
			Active = true,
			State = "active",
			SubState = "running",
			Pid = 4242,
			Instance = "",
		});

		Assert.False(snapshot.Identity.IsKnown);
		Assert.Equal(4242, snapshot.Pid);
	}

	[Fact]
	public void Состояние_systemd_сохраняется_дословно_и_разбирается_отдельно()
	{
		var snapshot = AgentServerRuntime.ToSnapshot(new ServiceStatusPayload
		{
			State = "reloading",
			SubState = "reload",
		});

		Assert.Equal("reloading", snapshot.StateText);
		Assert.Equal(RuntimeState.Starting, snapshot.State);
	}

	[Theory]
	[InlineData(false, true, typeof(TelemetryReading.Unavailable))]
	[InlineData(true, true, typeof(TelemetryReading.Stale))]
	[InlineData(true, false, typeof(TelemetryReading.Fresh))]
	public void Три_флага_телеметрии_превращаются_в_три_случая(bool available, bool stale, Type expected)
	{
		var reading = AgentTelemetryReader.ToReading(new TelemetryPayload
		{
			Available = available,
			Stale = stale,
			AgeSeconds = 120,
			GeneratedAt = 1758369600,
			Detail = "подробность",
		});

		Assert.IsType(expected, reading);
	}

	/// <summary>
	/// После перевода в закрытую иерархию нельзя получить «недоступна, но вот снимок»: у
	/// Unavailable снимка нет.
	/// </summary>
	[Fact]
	public void Недоступная_телеметрия_не_несёт_снимка()
	{
		var reading = AgentTelemetryReader.ToReading(new TelemetryPayload
		{
			Available = false,
			GeneratedAt = 1758369600,
			Players = [new PlayerPayload { Name = "кто-то" }],
		});

		var unavailable = Assert.IsType<TelemetryReading.Unavailable>(reading);
		Assert.Equal("Телеметрия недоступна", unavailable.Detail);
	}
}
