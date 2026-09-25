using Panel.SharedKernel;

namespace Panel.Content.Tests;

/// <summary>
/// Фразы подтверждения. Их набирает человек по подсказке фронтенда (ADR-16), и расхождение в
/// одной букве сделает кнопку неработающей.
/// </summary>
public sealed class ConfirmationTests
{
	[Fact]
	public void Удаление() =>
		Assert.Equal("УДАЛИТЬ world.vcdbs", Confirmation.ToDelete("world.vcdbs").Expected);

	[Fact]
	public void Архивация() =>
		Assert.Equal("АРХИВИРОВАТЬ world.vcdbs", Confirmation.ToArchive("world.vcdbs").Expected);

	[Fact]
	public void Восстановление() =>
		Assert.Equal("ВОССТАНОВИТЬ vintagestory-a.tar.gz", Confirmation.ToRestore("vintagestory-a.tar.gz").Expected);

	[Fact]
	public void Извлечение() =>
		Assert.Equal("ИЗВЛЕЧЬ new.vcdbs", Confirmation.ToExtract("new.vcdbs").Expected);

	/// <summary>Очистка относится к набору копий, и называть в ней нечего.</summary>
	[Fact]
	public void Очистка_копий() =>
		Assert.Equal("ОЧИСТИТЬ СТАРЫЕ КОПИИ", Confirmation.Exactly("ОЧИСТИТЬ СТАРЫЕ КОПИИ").Expected);

	/// <summary>
	/// Удаление мода — единственное подтверждение без глагола.
	/// </summary>
	[Fact]
	public void Удаление_мода_без_глагола() =>
		Assert.Equal("survival_1.0.zip", Confirmation.ByNameAlone("survival_1.0.zip").Expected);

	[Fact]
	public void Регистр_имеет_значение() =>
		Assert.False(Confirmation.ToDelete("world.vcdbs").Check("удалить world.vcdbs").IsSuccess);

	[Fact]
	public void Пустая_фраза_не_подходит() =>
		Assert.False(Confirmation.ToDelete("world.vcdbs").Check(null).IsSuccess);

	[Fact]
	public void Окружающие_пробелы_не_мешают() =>
		Assert.True(Confirmation.ToExtract("new.vcdbs").Check("\tИЗВЛЕЧЬ new.vcdbs \n").IsSuccess);
}
