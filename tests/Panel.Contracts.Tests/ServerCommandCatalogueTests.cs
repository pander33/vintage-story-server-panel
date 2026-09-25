using Panel.Contracts.ServerCommands;

namespace Panel.Contracts.Tests;

/// <summary>
/// Контрактные тесты каталога консольных команд: проверяются не только имена действий, но и
/// области значений.
/// </summary>
public sealed class ServerCommandCatalogueTests
{
	/// <summary>
	/// В шаблоне нет подстановок, кроме <c>{value}</c>: другая подстановка позволила бы собрать
	/// произвольную команду из внешних данных.
	/// </summary>
	[Fact]
	public void В_шаблонах_нет_подстановок_кроме_value()
	{
		foreach (var spec in ServerCommandCatalogue.All)
		{
			var withoutValue = spec.Template.Replace("{value}", string.Empty, StringComparison.Ordinal);

			Assert.DoesNotContain("{", withoutValue, StringComparison.Ordinal);
			Assert.DoesNotContain("}", withoutValue, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void Подстановка_есть_тогда_и_только_тогда_когда_есть_область_значений()
	{
		foreach (var spec in ServerCommandCatalogue.All)
		{
			var hasPlaceholder = spec.Template.Contains("{value}", StringComparison.Ordinal);

			Assert.Equal(hasPlaceholder, spec.Value is not null);
		}
	}

	/// <summary>
	/// Время суток задаётся пресетом или часом.
	/// </summary>
	[Theory]
	[InlineData("midnight", true)]
	[InlineData("MIDNIGHT", true)]
	[InlineData("0", true)]
	[InlineData("23.99", true)]
	[InlineData("23.9", true)]
	[InlineData("24", false)]
	[InlineData("23.999", false)]
	[InlineData("полночь", false)]
	[InlineData("-1", false)]
	public void Момент_суток_принимает_и_пресет_и_час(string value, bool accepted) =>
		Assert.Equal(accepted, Domain("time-set").Validate(value).IsSuccess);

	[Theory]
	[InlineData("0", true)]
	[InlineData("9999", true)]
	[InlineData("10000", false)]
	[InlineData("-1", false)]
	[InlineData("60.5", false)]
	[InlineData("быстро", false)]
	public void Скорость_времени_целая_и_в_пределах(string value, bool accepted) =>
		Assert.Equal(accepted, Domain("time-speed").Validate(value).IsSuccess);

	[Theory]
	[InlineData("0", true)]
	[InlineData("-1", true)]
	[InlineData("1", true)]
	[InlineData("0.5", true)]
	[InlineData("-0.125", true)]
	[InlineData("1.0001", false)]
	[InlineData("2", false)]
	public void Осадки_в_пределах_от_минус_одного_до_одного(string value, bool accepted) =>
		Assert.Equal(accepted, Domain("weather-precip").Validate(value).IsSuccess);

	[Theory]
	[InlineData("storm", true)]
	[InlineData("Storm", true)]
	[InlineData("ураган", false)]
	public void Ветер_выбирается_из_списка(string value, bool accepted) =>
		Assert.Equal(accepted, Domain("weather-wind").Validate(value).IsSuccess);

	/// <summary>
	/// Слишком длинное значение отвергается, а не обрезается: обрезанное прошло бы проверку и
	/// ушло на сервер в другом виде.
	/// </summary>
	[Fact]
	public void Слишком_длинное_значение_отвергается() =>
		Assert.False(Domain("weather-wind").Validate(new string('a', ValueDomain.MaxLength + 1)).IsSuccess);

	[Fact]
	public void Выбор_нормализуется_к_каноническому_написанию() =>
		Assert.Equal("storm", Domain("weather-wind").Validate("STORM").Value);

	[Fact]
	public void Каждое_действие_описано_для_интерфейса()
	{
		foreach (var spec in ServerCommandCatalogue.All)
		{
			Assert.False(string.IsNullOrWhiteSpace(spec.Label), spec.Action);
			Assert.False(string.IsNullOrWhiteSpace(spec.Group), spec.Action);
			Assert.False(string.IsNullOrWhiteSpace(spec.Hint), spec.Action);
		}
	}

	[Fact]
	public void Неизвестное_действие_не_находится() =>
		Assert.False(ServerCommandCatalogue.Find("нет-такого").IsSuccess);

	private static ValueDomain Domain(string action) => ServerCommandCatalogue.Find(action).Value.Value!;
}
