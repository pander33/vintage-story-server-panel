using Panel.Web.Application;

namespace Panel.Web.Tests;

/// <summary>
/// Ограничение входа блокирует перебор и не копит адреса: панель в интернете видит тысячи
/// случайных адресов, и запись на каждый росла бы без предела.
/// </summary>
public sealed class LoginThrottleTests
{
	private readonly ManualClock clock = new();

	[Fact]
	public void Восемь_неудач_за_окно_блокируют_адрес()
	{
		var throttle = new LoginThrottle(clock);

		for (var attempt = 0; attempt < LoginThrottle.MaxAttempts; attempt++)
		{
			Assert.False(throttle.IsBlocked("203.0.113.5"));
			throttle.RecordFailure("203.0.113.5");
		}

		Assert.True(throttle.IsBlocked("203.0.113.5"));
		Assert.False(throttle.IsBlocked("203.0.113.6"));

		clock.Advance(LoginThrottle.Window + TimeSpan.FromSeconds(1));
		Assert.False(throttle.IsBlocked("203.0.113.5"));
	}

	[Fact]
	public void Проверка_и_успешный_вход_не_оставляют_записи()
	{
		var throttle = new LoginThrottle(clock);

		Assert.False(throttle.IsBlocked("203.0.113.5"));
		throttle.RecordFailure("203.0.113.6");
		throttle.Forget("203.0.113.6");

		Assert.Equal(0, throttle.Tracked);
	}

	[Fact]
	public void Адреса_без_свежих_неудач_выметаются()
	{
		var throttle = new LoginThrottle(clock);
		for (var host = 0; host < 1000; host++)
		{
			throttle.RecordFailure($"198.51.100.{host % 250}:{host}");
		}

		clock.Advance(LoginThrottle.Window + TimeSpan.FromSeconds(1));
		throttle.RecordFailure("203.0.113.5");

		Assert.Equal(1, throttle.Tracked);
	}

	private sealed class ManualClock : TimeProvider
	{
		private DateTimeOffset now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

		public override DateTimeOffset GetUtcNow() => now;

		public void Advance(TimeSpan by) => now += by;
	}
}
