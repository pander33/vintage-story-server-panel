using Panel.Observability.Domain;

namespace Panel.Observability.Tests;

/// <summary>
/// Разбор строк журнала в формате <c>journalctl --output=short-iso</c>.
/// </summary>
public sealed class LogEventTests
{
	private const string prefix = "2026-09-21T14:30:00+0300 host vintagestory[1234]: ";

	[Theory]
	[InlineData("[Error] Something broke", LogCategory.Error)]
	[InlineData("Unhandled Exception: System.NullReferenceException", LogCategory.Error)]
	[InlineData("vs.service: Failed with result 'exit-code'.", LogCategory.Error)]
	[InlineData("Player Alice got removed.", LogCategory.Player)]
	[InlineData("Client 3 disconnected", LogCategory.Player)]
	[InlineData("Alice joins.", LogCategory.Player)]
	[InlineData("Loaded 42 mods", LogCategory.Mod)]
	[InlineData("[carryon] Patching done", LogCategory.Mod)]
	public void Категория_по_правилам_журнала(string message, LogCategory expected) =>
		Assert.Equal(expected, LogEvent.FromLine(prefix + message).Category);

	/// <summary>
	/// Известный дефект правил (findings §31): шаблон мода <c>\[[a-z0-9_.-]+\]</c> совпадает с
	/// <c>[1234]</c> из <c>ident[pid]:</c>. Поэтому любая строка игры без признаков ошибки или игрока
	/// попадает в «мод», а в «систему» — только строки без pid. Исправление изменит фильтр вкладки
	/// «Журнал», и решать это должен владелец.
	/// </summary>
	[Fact]
	public void Строка_с_pid_попадает_в_моды()
	{
		Assert.Equal(LogCategory.Mod, LogEvent.FromLine(prefix + "Saving world").Category);
		Assert.Equal(LogCategory.System, LogEvent.FromLine("2026-09-21T14:30:00+0300 host systemd: Saving world").Category);
	}

	/// <summary>Ошибка важнее игрока: строка с обоими признаками считается ошибкой.</summary>
	[Fact]
	public void Ошибка_важнее_игрока() =>
		Assert.Equal(LogCategory.Error, LogEvent.FromLine(prefix + "[Error] Player Alice crashed").Category);

	[Theory]
	[InlineData("[Server Event] Alice [::1]:52012 joins.", true, "Alice")]
	[InlineData("[Server Event] Bob_2 joins.", true, "Bob_2")]
	[InlineData("[Server Notification] Player Carol left.", false, "Carol")]
	[InlineData("[Server Notification] Player Dave got removed.", false, "Dave")]
	[InlineData("UDP: client disconnected Erin", false, "Erin")]
	public void Вход_и_выход_игрока(string message, bool joined, string player)
	{
		var change = LogEvent.FromLine(prefix + message).PlayerChange;

		Assert.NotNull(change);
		Assert.Equal(joined, change.Joined);
		Assert.Equal(player, change.Player);
		Assert.Equal(joined ? "join" : "leave", change.Type);
	}

	[Fact]
	public void Обычная_строка_не_событие_игрока() =>
		Assert.Null(LogEvent.FromLine(prefix + "Saving world").PlayerChange);

	[Theory]
	[InlineData("Server overloaded, skipping 3 ticks", true)]
	[InlineData("Tick took too long", true)]
	[InlineData("Tick finished", false)]
	public void Перегрузка(string message, bool expected) =>
		Assert.Equal(expected, LogEvent.FromLine(prefix + message).Overload);

	/// <summary><c>fromisoformat</c> принимает смещение и с двоеточием, и без.</summary>
	[Theory]
	[InlineData("2026-09-21T14:30:00+0300 host x: y", 1789990200L)]
	[InlineData("2026-09-21T14:30:00+03:00 host x: y", 1789990200L)]
	[InlineData("2026-09-21T11:30:00Z host x: y", 1789990200L)]
	[InlineData("2026-09-21T14:30:00.123456+0300 host x: y", 1789990200L)]
	public void Метка_времени_из_начала_строки(string line, long expected) =>
		Assert.Equal(expected, LogEvent.FromLine(line).Timestamp);

	[Theory]
	[InlineData("Sep 21 14:30:00 host x: y")]
	[InlineData("short line")]
	[InlineData("")]
	public void Без_метки_времени_пусто(string line) =>
		Assert.Null(LogEvent.FromLine(line).Timestamp);

	[Theory]
	[InlineData("all", "", 4)]
	[InlineData("error", "", 1)]
	[InlineData("player", "", 2)]
	[InlineData("all", "ALICE", 1)]
	[InlineData("player", "  bob ", 1)]
	public void Фильтр_по_категории_и_подстроке(string category, string search, int expected)
	{
		var events = new[]
		{
			LogEvent.FromLine(prefix + "[Error] boom"),
			LogEvent.FromLine(prefix + "Alice joins."),
			LogEvent.FromLine(prefix + "Bob joins."),
			LogEvent.FromLine(prefix + "Saving world"),
		};

		Assert.Equal(expected, LogFilter.Create(category, search).Value.Apply(events).Count);
	}

	[Fact]
	public void Неизвестная_категория_отвергается() =>
		Assert.Equal("Неизвестная категория журнала", LogFilter.Create("warn", "").Failure!.Message);

	[Fact]
	public void Длинный_поиск_отвергается() =>
		Assert.Equal("Поисковая строка слишком длинная", LogFilter.Create("all", new string('x', 121)).Failure!.Message);

	/// <summary>Строки одного сбоя идут подряд и считаются одним сбоем, его строка — последняя.</summary>
	[Fact]
	public void Сбои_рядом_сливаются_в_один()
	{
		var events = Lines(
			"Unhandled Exception: boom",
			"   at Foo.Bar()",
			"vs.service: Main process exited, code=exited",
			"Saving", "Saving", "Saving", "Saving", "Saving", "Saving",
			"Critical error in tick");

		var summary = CrashSummary.Of(events);

		Assert.Equal(2, summary.Count);
		Assert.False(summary.Repeated);
		Assert.EndsWith("Critical error in tick", summary.Last);
	}

	[Fact]
	public void Три_сбоя_это_повтор()
	{
		var filler = Enumerable.Repeat("Saving", 6).ToArray();
		var events = Lines(["segmentation fault", .. filler, "segmentation fault", .. filler, "core dump"]);

		Assert.True(CrashSummary.Of(events).Repeated);
	}

	[Fact]
	public void Без_сбоев_пусто() =>
		Assert.Equal(new CrashSummary(0, false, null), CrashSummary.Of(Lines("Saving")));

	/// <summary>Строка начала сессии сбрасывает список игроков прошлого процесса.</summary>
	[Fact]
	public void Игроки_онлайн_по_журналу()
	{
		var events = Lines(
			"Ghost joins.",
			"Dedicated Server now running on Port 42420",
			"bob joins.",
			"Alice joins.",
			"Player bob left.",
			"Carol joins.");

		var online = OnlinePlayers.Infer(events);

		Assert.Equal(["Alice", "Carol"], online.Select(player => player.Name));
		Assert.EndsWith("Alice joins.", online[0].Since);
	}

	private static List<LogEvent> Lines(params string[] messages) =>
		[.. messages.Select(message => LogEvent.FromLine(prefix + message))];
}
