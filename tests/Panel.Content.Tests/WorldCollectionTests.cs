using Panel.Content.Domain;
using Panel.SharedKernel;

namespace Panel.Content.Tests;

/// <summary>
/// Правила набора миров: работающая служба и активный мир.
/// </summary>
public sealed class WorldCollectionTests
{
	[Fact]
	public void При_работающем_сервере_состав_миров_не_меняется()
	{
		var refusal = Collection(running: true).CanModify();

		Assert.False(refusal.IsSuccess);
		Assert.Equal(FailureCode.Conflict, refusal.Failure!.Code);
	}

	[Fact]
	public void Активный_мир_нельзя_убрать()
	{
		var refusal = Collection(running: false).CanRemove(WorldFileName.Parse("active.vcdbs"));

		Assert.False(refusal.IsSuccess);
		Assert.Equal(FailureCode.Conflict, refusal.Failure!.Code);
	}

	[Fact]
	public void Неактивный_мир_убрать_можно() =>
		Assert.True(Collection(running: false).CanRemove(WorldFileName.Parse("old.vcdbs")).IsSuccess);

	/// <summary>
	/// Работающая служба проверяется раньше существования мира, иначе вместо настоящей причины
	/// пользователь увидел бы «мир не найден».
	/// </summary>
	[Fact]
	public void Служба_проверяется_раньше_существования_мира()
	{
		var refusal = Collection(running: true).CanRemove(WorldFileName.Parse("нет-такого.vcdbs"));

		Assert.False(refusal.IsSuccess);
		Assert.Contains("Остановите сервер", refusal.Failure!.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Несуществующий_мир_не_находится()
	{
		var refusal = Collection(running: false).Find(WorldFileName.Parse("нет-такого.vcdbs"));

		Assert.False(refusal.IsSuccess);
		Assert.Equal(FailureCode.NotFound, refusal.Failure!.Code);
	}

	[Fact]
	public void Занятое_имя_не_годится_для_нового_мира()
	{
		var refusal = Collection(running: false).CanAdd(WorldFileName.Parse("old.vcdbs"));

		Assert.False(refusal.IsSuccess);
		Assert.Equal(FailureCode.Conflict, refusal.Failure!.Code);
	}

	[Fact]
	public void Свободное_имя_годится() =>
		Assert.True(Collection(running: false).CanAdd(WorldFileName.Parse("new.vcdbs")).IsSuccess);

	/// <summary>
	/// Фраза подтверждения различает архив и корзину, хотя на диске это одно и то же перемещение.
	/// </summary>
	[Fact]
	public void Фраза_подтверждения_зависит_от_назначения()
	{
		var world = WorldFileName.Parse("old.vcdbs");

		Assert.Equal("АРХИВИРОВАТЬ old.vcdbs",
			WorldDispositionWire.ConfirmationFor(WorldDisposition.Archived, world).Expected);
		Assert.Equal("УДАЛИТЬ old.vcdbs",
			WorldDispositionWire.ConfirmationFor(WorldDisposition.Trashed, world).Expected);
	}

	[Fact]
	public void Неверная_фраза_не_принимается() =>
		Assert.False(Confirmation.ToDelete("old.vcdbs").Check("удалить old.vcdbs").IsSuccess);

	[Fact]
	public void Верная_фраза_принимается() =>
		Assert.True(Confirmation.ToDelete("old.vcdbs").Check("  УДАЛИТЬ old.vcdbs  ").IsSuccess);

	/// <summary>
	/// Панель проверяет только форму кода: набор стилей объявляют игра и моды, и есть ли стиль на
	/// сервере, решает демон по схеме этого сервера.
	/// </summary>
	[Theory]
	[InlineData("surviveandbuild", true)]
	[InlineData("creativebuilding", true)]
	[InlineData("mymod-hardcore", true)]
	[InlineData("", false)]
	[InlineData("../etc", false)]
	[InlineData("with space", false)]
	public void Стиль_игры_проверяется_по_форме_кода(string value, bool accepted) =>
		Assert.Equal(accepted, PlayStyle.Create(value).IsSuccess);

	[Fact]
	public void Без_схемы_код_языка_по_прежнему_правилу() =>
		Assert.Equal("preset-exploration", PlayStyle.Create("exploration").Value.LanguageCode);

	/// <summary>У творческого стиля игры 1.22.7 ключ подписи без приставки — берётся из схемы.</summary>
	[Fact]
	public void Объявленный_стиль_несёт_ключ_подписи_схемы()
	{
		var style = PlayStyle.CreateDeclared("creativebuilding", "creativebuilding").Value;

		Assert.Equal("creativebuilding", style.Code);
		Assert.Equal("creativebuilding", style.LanguageCode);
	}

	[Fact]
	public void Встроенный_набор_совпадает_со_стилями_игры() =>
		Assert.Equal(["surviveandbuild", "exploration", "wildernesssurvival", "homosapiens", "creativebuilding"], PlayStyle.Builtin);

	[Theory]
	[InlineData("archive", true)]
	[InlineData("trash", true)]
	[InlineData("delete", false)]
	public void Назначение_мира(string value, bool accepted) =>
		Assert.Equal(accepted, WorldDispositionWire.FromWire(value).IsSuccess);

	[Fact]
	public void Подпись_мира_узнаётся()
	{
		Assert.True(WorldDatabase.CheckSignature("SQLite format 3\u0000"u8).IsSuccess);
		Assert.False(WorldDatabase.CheckSignature("SQLite format 2\u0000"u8).IsSuccess);
		Assert.False(WorldDatabase.CheckSignature("SQLite"u8).IsSuccess);
	}

	private static WorldCollection Collection(bool running) =>
		WorldCollection.Of(
		[
			new WorldFile(WorldFileName.Parse("active.vcdbs"), 1024, DateTimeOffset.UnixEpoch.AddDays(2), Active: true),
			new WorldFile(WorldFileName.Parse("old.vcdbs"), 512, DateTimeOffset.UnixEpoch, Active: false),
		],
		running);
}
