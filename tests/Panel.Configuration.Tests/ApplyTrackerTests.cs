using System.Text.Json.Nodes;
using Panel.Configuration.Domain;

namespace Panel.Configuration.Tests;

/// <summary>
/// Все ветки трекера применения. Порядок веток важен, поэтому каждая проверяется отдельно.
/// </summary>
public sealed class ApplyTrackerTests
{
	private const string process = "boot:1234:5678";

	private static readonly DateTimeOffset now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

	private static readonly ServerConfiguration saved =
		ServerConfiguration.FromJson(JsonNode.Parse("{\"Port\":42420}")).Value;

	private static readonly IReadOnlyList<ConfigChange> portChanged =
		[new("Port", "Порт", 1, 42420, RestartRequired: true)];

	[Fact]
	public void Отметка_хранит_только_ключи_требующие_перезапуска()
	{
		var marker = ApplyTracker.Mark(
			saved,
			[.. portChanged, new("OnlyWhitelisted", "…", false, true, RestartRequired: false)],
			Running(process),
			now);

		Assert.Equal(["Port"], marker.ChangedKeys);
		Assert.True(marker.RequiresRestart);
		Assert.Equal(saved.Revision.Value, marker.Revision);
		Assert.Equal(process, marker.ServiceInstance);
		Assert.Equal(now.ToUnixTimeSeconds(), marker.SavedAt);
	}

	[Fact]
	public void Остановленный_процесс_в_отметку_не_попадает() =>
		Assert.Null(ApplyTracker.Mark(saved, portChanged, ServiceObservation.Stopped, now).ServiceInstance);

	[Fact]
	public void Остановленная_служба_требует_запуска()
	{
		var tracked = ApplyTracker.Inspect(Marker(process), saved, 0, ServiceObservation.Stopped);
		var untracked = ApplyTracker.Inspect(null, saved, 0, ServiceObservation.Stopped);

		Assert.Equal((ApplyStateKind.StartRequired, ApplySource.Tracked), (tracked.State, tracked.Source));
		Assert.Equal((ApplyStateKind.StartRequired, ApplySource.Service), (untracked.State, untracked.Source));
		Assert.Equal(["Port"], tracked.ChangedKeys);
		Assert.Empty(untracked.ChangedKeys);
	}

	[Fact]
	public void Тот_же_процесс_требует_перезапуска()
	{
		var state = ApplyTracker.Inspect(Marker(process), saved, 0, Running(process));

		Assert.Equal(ApplyStateKind.RestartRequired, state.State);
		Assert.Equal(ApplySource.Tracked, state.Source);
		Assert.True(state.RestartRequired);
		Assert.Equal(now.ToUnixTimeSeconds(), state.SavedAt);
	}

	/// <summary>Ручной <c>systemctl restart</c> в обход панели замечается по смене идентичности.</summary>
	[Fact]
	public void Другой_процесс_значит_применено() =>
		Assert.Equal(
			(ApplyStateKind.Applied, ApplySource.Tracked),
			Pair(ApplyTracker.Inspect(Marker(process), saved, 0, Running("boot:9999:1"))));

	[Fact]
	public void Изменения_без_перезапуска_применены_сразу()
	{
		var marker = Marker(process) with { ChangedKeys = [], RequiresRestart = false };

		Assert.Equal(
			(ApplyStateKind.Applied, ApplySource.Tracked),
			Pair(ApplyTracker.Inspect(marker, saved, 0, Running(process))));
	}

	/// <summary>
	/// Сохранено при остановленной службе: в отметке процесса нет, а сейчас он есть, значит,
	/// запущен уже с новыми настройками.
	/// </summary>
	[Fact]
	public void Сохранено_при_остановленной_и_запущено_позже() =>
		Assert.Equal(
			(ApplyStateKind.Applied, ApplySource.Tracked),
			Pair(ApplyTracker.Inspect(Marker(null), saved, 0, Running(process))));

	[Fact]
	public void Чужая_отметка_не_учитывается()
	{
		var foreign = Marker(process) with { Revision = "другая" };
		var state = ApplyTracker.Inspect(foreign, saved, 0, Running(process, now));

		Assert.Equal((ApplyStateKind.Applied, ApplySource.Service), Pair(state));
		Assert.Empty(state.ChangedKeys);
		Assert.Null(state.SavedAt);
	}

	[Fact]
	public void Файл_правленый_после_старта_требует_перезапуска()
	{
		var modified = Nanoseconds(now.AddSeconds(6));

		Assert.Equal(
			(ApplyStateKind.RestartRequired, ApplySource.FileTime),
			Pair(ApplyTracker.Inspect(null, saved, modified, Running(process, now))));
	}

	/// <summary>Игра переписывает свой файл при старте, и это не считается правкой администратора.</summary>
	[Fact]
	public void Запись_игрой_при_старте_не_считается_правкой()
	{
		var modified = Nanoseconds(now.AddSeconds(4));

		Assert.Equal(
			(ApplyStateKind.Applied, ApplySource.Service),
			Pair(ApplyTracker.Inspect(null, saved, modified, Running(process, now))));
	}

	[Fact]
	public void Без_времени_старта_судить_не_о_чем() =>
		Assert.Equal(
			(ApplyStateKind.Applied, ApplySource.Service),
			Pair(ApplyTracker.Inspect(null, saved, long.MaxValue, Running(process))));

	private static ApplyMarker Marker(string? process) =>
		ApplyTracker.Mark(saved, portChanged, process is null ? ServiceObservation.Stopped : Running(process), now);

	private static ServiceObservation Running(string process, DateTimeOffset? started = null) =>
		new(true, process, started);

	private static (ApplyStateKind, ApplySource) Pair(ApplyState state) => (state.State, state.Source);

	private static long Nanoseconds(DateTimeOffset moment) => moment.ToUnixTimeMilliseconds() * 1_000_000L;
}
