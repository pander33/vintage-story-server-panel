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
///
/// Адрес хранится, только пока у него есть неудачи в окне: проверка записи не заводит, а
/// адреса без свежих неудач выметаются раз в окно. Иначе панель в интернете копила бы запись на
/// каждый адрес, с которого её когда-либо пробовали, и память росла бы без предела.
/// Запросов входа мало, поэтому блокировка одна на всё.
/// </remarks>
public sealed class LoginThrottle
{
	public const int MaxAttempts = 8;

	public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

	private readonly Lock gate = new();
	private readonly Dictionary<string, List<DateTimeOffset>> attempts = new(StringComparer.Ordinal);
	private readonly TimeProvider time;
	private DateTimeOffset swept;

	public LoginThrottle(TimeProvider time)
	{
		this.time = time;
		swept = time.GetUtcNow();
	}

	/// <summary>Сколько адресов сейчас помнит ограничение.</summary>
	public int Tracked
	{
		get
		{
			lock (gate)
			{
				return attempts.Count;
			}
		}
	}

	public bool IsBlocked(string address)
	{
		var now = time.GetUtcNow();
		lock (gate)
		{
			if (!attempts.TryGetValue(address, out var bucket))
			{
				return false;
			}

			bucket.RemoveAll(moment => moment < now - Window);
			if (bucket.Count == 0)
			{
				attempts.Remove(address);
				return false;
			}

			return bucket.Count >= MaxAttempts;
		}
	}

	public void RecordFailure(string address)
	{
		var now = time.GetUtcNow();
		lock (gate)
		{
			if (now - swept >= Window)
			{
				swept = now;
				foreach (var (known, bucket) in attempts)
				{
					if (bucket.Count == 0 || bucket[^1] < now - Window)
					{
						attempts.Remove(known);
					}
				}
			}

			if (!attempts.TryGetValue(address, out var current))
			{
				current = [];
				attempts[address] = current;
			}

			current.Add(now);
		}
	}

	public void Forget(string address)
	{
		lock (gate)
		{
			attempts.Remove(address);
		}
	}
}
