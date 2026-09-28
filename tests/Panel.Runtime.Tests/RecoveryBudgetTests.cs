using System.Text.Json.Nodes;
using Panel.Runtime.Domain;
using Panel.SharedKernel;

namespace Panel.Runtime.Tests;

/// <summary>
/// Частота автоперезапусков. Право на перезапуск даёт вердикт, бюджет только ограничивает.
/// </summary>
public sealed class RecoveryBudgetTests
{
	private static readonly DateTimeOffset now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

	private static readonly WatchdogSettings settings = WatchdogSettings.Default with { AutoRestart = true };

	[Fact]
	public void Первый_перезапуск_разрешён()
	{
		Assert.Null(RecoveryBudget.Blocker([], settings, now));
	}

	[Fact]
	public void Пауза_между_перезапусками_соблюдается()
	{
		var recent = RecoveryBudget.Recent([now.AddMinutes(-5)], now);

		Assert.Equal("Действует пауза между автоперезапусками", RecoveryBudget.Blocker(recent, settings, now)?.ToString());
		Assert.Null(RecoveryBudget.Blocker(recent, settings, now.AddMinutes(11)));
	}

	[Fact]
	public void Часовой_лимит_проверяется_раньше_паузы()
	{
		var recent = RecoveryBudget.Recent([now.AddMinutes(-50), now.AddMinutes(-30), now.AddMinutes(-1)], now);

		Assert.Equal("Достигнут часовой лимит автоперезапусков", RecoveryBudget.Blocker(recent, settings, now)?.ToString());
	}

	[Fact]
	public void Перезапуски_старше_часа_забываются()
	{
		var recent = RecoveryBudget.Recent(
			[now.AddMinutes(-90), now.AddMinutes(-61), now.AddMinutes(-20)],
			now);

		Assert.Equal([now.AddMinutes(-20)], recent);
		Assert.Null(RecoveryBudget.Blocker(recent, settings, now)?.ToString());
	}

	[Fact]
	public void Автоперезапуск_выключен_по_умолчанию_даже_из_пустого_тела()
	{
		var settings = WatchdogSettingsJson.FromJson(new JsonObject()).Value;

		Assert.False(settings.AutoRestart);
		Assert.True(settings.Enabled);
		Assert.Equal(
			"""{"enabled":true,"auto_restart":false,"startup_timeout":300,"stale_seconds":45,"failure_threshold":3,"cooldown_seconds":900,"max_restarts_hour":3}""",
			WatchdogSettingsJson.ToJson(settings).ToJsonString());
	}

	[Fact]
	public void Значение_вне_предела_отклоняется_замороженным_текстом()
	{
		var settings = WatchdogSettingsJson.FromJson(new JsonObject { ["cooldown_seconds"] = 60 });

		Assert.Equal("Недопустимое значение cooldown_seconds: требуется 300–86400", settings.Failure!.Message);
	}
}
