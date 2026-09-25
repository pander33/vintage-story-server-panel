using System.Collections.Concurrent;

namespace Panel.Web.Application;

/// <summary>
/// Ограничение попыток входа: восемь за пять минут с одного адреса.
/// </summary>
/// <remarks>
/// Счётчик обнуляется при успешном входе, а не по истечении окна, иначе один верный пароль не
/// снимал бы ограничение, вызванное чужими попытками с того же адреса (например, за общим
/// NAT).
///
/// Состояние хранится в памяти, и перезапуск панели снимает ограничение: оно защищает от
/// перебора, а не от того, кто может перезапускать службу.
/// </remarks>
public sealed class LoginThrottle
{
	public const int MaxAttempts = 8;

	public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

	private readonly ConcurrentDictionary<string, List<DateTimeOffset>> attempts = new(StringComparer.Ordinal);
	private readonly TimeProvider time;

	public LoginThrottle(TimeProvider time) => this.time = time;

	public bool IsBlocked(string address)
	{
		var now = time.GetUtcNow();
		var bucket = attempts.GetOrAdd(address, static _ => []);

		lock (bucket)
		{
			bucket.RemoveAll(moment => moment < now - Window);
			return bucket.Count >= MaxAttempts;
		}
	}

	public void RecordFailure(string address)
	{
		var bucket = attempts.GetOrAdd(address, static _ => []);
		lock (bucket)
		{
			bucket.Add(time.GetUtcNow());
		}
	}

	public void Forget(string address) => attempts.TryRemove(address, out _);
}
