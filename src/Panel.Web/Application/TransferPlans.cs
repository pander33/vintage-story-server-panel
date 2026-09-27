using System.Collections.Concurrent;
using System.Security.Cryptography;
using Panel.Contracts.Agent;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>Проверенный переносимый пакет, привязанный к сессии и манифесту.</summary>
public sealed record TransferPlan(StagedFile File, string Sha256, long Length, string ActiveWorld, TransferInspectionPayload Inspection);

/// <summary>Одноразовые планы импорта; истёкшие файлы удаляются при следующем запросе.</summary>
public sealed class TransferPlans(StagingWriter staging, TimeProvider clock)
{
	private readonly ConcurrentDictionary<string, Entry> plans = new(StringComparer.Ordinal);
	private readonly record struct Entry(TransferPlan Plan, string Session, DateTimeOffset Expires);

	public string Add(TransferPlan plan, string session)
	{
		Sweep();
		var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
		plans[token] = new Entry(plan, session, clock.GetUtcNow().AddMinutes(30));
		return token;
	}

	/// <summary>План сессии без его изъятия; <c>null</c>, если его нет, он истёк или чужой.</summary>
	public TransferPlan? Peek(string? token, string session)
	{
		Sweep();
		return token is not null && plans.TryGetValue(token, out var entry) && SessionMatches(entry.Session, session)
			? entry.Plan
			: null;
	}

	public Result<TransferPlan> Take(string? token, string session)
	{
		Sweep();
		if (token is null || !plans.TryGetValue(token, out var entry)
			|| !SessionMatches(entry.Session, session)
			|| !plans.TryRemove(new KeyValuePair<string, Entry>(token, entry)))
		{
			return Failure.Conflict("План импорта отсутствует, истёк или принадлежит другой сессии");
		}

		return entry.Plan;
	}

	private static bool SessionMatches(string expected, string actual) =>
		CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(actual));

	private void Sweep()
	{
		foreach (var pair in plans)
		{
			if (pair.Value.Expires <= clock.GetUtcNow() && plans.TryRemove(new KeyValuePair<string, Entry>(pair.Key, pair.Value)))
			{
				staging.Discard(pair.Value.Plan.File);
			}
		}
	}
}
