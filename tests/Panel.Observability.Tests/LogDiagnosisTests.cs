using Panel.Observability.Domain;
using Panel.SharedKernel;

namespace Panel.Observability.Tests;

/// <summary>Объяснения по журналу.</summary>
public sealed class LogDiagnosisTests
{
	private const string prefix = "2026-09-21T14:30:00+0300 host vintagestory[1234]: ";

	[Fact]
	public void Готовый_сервер_ничего_не_ищет()
	{
		var diagnosis = LogDiagnosis.Diagnose(true, true, WatchdogNote.None, Lines("Address already in use"));

		Assert.Equal(("ok", "ready"), (diagnosis.Startup.Level, diagnosis.Startup.Code));
	}

	[Theory]
	[InlineData("System.UnauthorizedAccessException: Access to the path '/srv/serverconfig.json' is denied.", "config-permission")]
	[InlineData("SocketException: Address already in use", "port-busy")]
	[InlineData("Out of memory.", "memory")]
	[InlineData("You must install or update .NET to run this application. hostfxr", "runtime")]
	[InlineData("Exception in LoadConfig", "config-invalid")]
	[InlineData("Mod carryon failed to load", "mod-startup")]
	[InlineData("vs.service: Main process exited, code=killed", "startup-crash")]
	public void Причина_запуска(string line, string code)
	{
		var diagnosis = LogDiagnosis.Diagnose(false, false, WatchdogNote.None, Lines(line));

		Assert.Equal("error", diagnosis.Startup.Level);
		Assert.Equal(code, diagnosis.Startup.Code);
		Assert.EndsWith(line, diagnosis.Startup.Evidence);
	}

	/// <summary>Ошибка прошлого процесса не объясняет текущий, поиск идёт только после старта.</summary>
	[Fact]
	public void Причина_ищется_только_в_текущей_сессии()
	{
		var events = Lines("Address already in use", "Server logger started", "Saving world");
		var diagnosis = LogDiagnosis.Diagnose(false, false, WatchdogNote.None, events);

		Assert.Equal("stopped", diagnosis.Startup.Code);
	}

	[Fact]
	public void Запущен_но_не_готов_берёт_пояснение_наблюдателя()
	{
		var diagnosis = LogDiagnosis.Diagnose(true, false, new WatchdogNote(Phrase.From("Телеметрия устарела")), Lines("Saving"));

		Assert.Equal(("warn", "not-ready", "Телеметрия устарела"),
			(diagnosis.Startup.Level, diagnosis.Startup.Code, diagnosis.Startup.Detail.ToString()));
	}

	[Fact]
	public void Отказы_подключения_с_конца_без_повторов_не_больше_пяти()
	{
		var events = Lines(
			"Player X is not on the whitelist",
			"Player X is not on the whitelist",
			"Client Y is banned",
			"Wrong password for Z",
			"Server is full",
			"Connection rejected for W",
			"Incompatible game version from V",
			"Saving");

		var connections = LogDiagnosis.Diagnose(true, true, WatchdogNote.None, events).Connections;

		Assert.Equal(5, connections.Count);
		Assert.Equal("version", connections[0].Code);
		Assert.DoesNotContain(connections, finding => finding.Code == "whitelist");
	}

	[Fact]
	public void Улика_обрезается_до_800_символов_с_конца()
	{
		var line = new string('x', 900) + " Address already in use";
		var evidence = LogDiagnosis.Diagnose(false, false, WatchdogNote.None, Lines(line)).Startup.Evidence!;

		Assert.Equal(800, evidence.Length);
		Assert.EndsWith("Address already in use", evidence);
	}

	private static List<LogEvent> Lines(params string[] messages) =>
		[.. messages.Select(message => LogEvent.FromLine(prefix + message))];
}
