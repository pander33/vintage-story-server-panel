using Panel.Content.Domain;

namespace Panel.Content.Tests;

/// <summary>История «на всякий случай»: правила очистки и порядок списка.</summary>
public sealed class HistoryTests
{
	private static readonly DateTimeOffset now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

	/// <summary>«Оставить N» действует в каждой группе: пять версий одного мода не вытесняют другой мод.</summary>
	[Fact]
	public void Очистка_оставляет_новейшие_в_каждой_группе()
	{
		var entries = new[]
		{
			Entry("carryon/1.zip", "carryon", hoursAgo: 3),
			Entry("carryon/2.zip", "carryon", hoursAgo: 2),
			Entry("carryon/3.zip", "carryon", hoursAgo: 1),
			Entry("jackson/1.zip", "jackson", hoursAgo: 10),
		};

		var removed = HistoryRules.Prune(entries, HistoryKeep.Create(2).Value);

		Assert.Equal(["carryon/1.zip"], removed.Select(entry => entry.Key));
	}

	[Fact]
	public void Порядок_по_группе_без_регистра_новые_первыми()
	{
		var ordered = HistoryRules.Ordered(
		[
			Entry("b", "Beta", hoursAgo: 1),
			Entry("a1", "alpha", hoursAgo: 5),
			Entry("a2", "alpha", hoursAgo: 1),
		]);

		Assert.Equal(["a2", "a1", "b"], ordered.Select(entry => entry.Key));
	}

	[Theory]
	[InlineData(0, false)]
	[InlineData(1, true)]
	[InlineData(100, true)]
	[InlineData(101, false)]
	public void Сколько_оставлять(int count, bool accepted) =>
		Assert.Equal(accepted, HistoryKeep.Create(count).IsSuccess);

	[Theory]
	[InlineData("carryon/1-carryon_1.0.zip", true)]
	[InlineData("serverconfig.json.panel-20260921-120000.bak", true)]
	[InlineData("../secret", false)]
	[InlineData("a/b/c", false)]
	[InlineData("a\\b", false)]
	[InlineData("", false)]
	public void Ключ_записи(string key, bool accepted) =>
		Assert.Equal(accepted, HistoryKey.Create(key).IsSuccess);

	[Theory]
	[InlineData("mod-history", true)]
	[InlineData("config-backup", true)]
	[InlineData("mod-config-backup", true)]
	[InlineData("backups", false)]
	public void Хранилища(string store, bool known) =>
		Assert.Equal(known, HistoryStoreWire.Parse(store).IsSuccess);

	private static HistoryEntry Entry(string key, string group, int hoursAgo) =>
		new(key, group, key, 10, now.AddHours(-hoursAgo));
}
