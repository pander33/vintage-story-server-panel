using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Fleet.Tests;

/// <summary>Правила реестра, не зависящие от файла.</summary>
public sealed class RegistryRulesTests
{
	private static readonly InstanceId main = InstanceId.Parse("main");
	private static readonly InstanceId second = InstanceId.Parse("second");
	private static readonly InstanceId panel = InstanceId.Parse("panel");

	[Fact]
	public void Названный_и_зарегистрированный_сервер_выбирается() =>
		Assert.Equal(second, RegistryRules.Resolve(Registry(main), second, panel));

	/// <summary>Незнакомое имя даёт умолчание: реестр мог измениться, пока открыта вкладка.</summary>
	[Fact]
	public void Незнакомое_имя_означает_умолчание_реестра() =>
		Assert.Equal(main, RegistryRules.Resolve(Registry(main), InstanceId.Parse("gone"), panel));

	[Fact]
	public void Без_имени_умолчание_реестра() =>
		Assert.Equal(main, RegistryRules.Resolve(Registry(main), null, panel));

	/// <summary>Умолчание, которого нет в реестре, не используется.</summary>
	[Fact]
	public void Объявленное_но_отсутствующее_умолчание_заменяется_panel_instance() =>
		Assert.Equal(panel, RegistryRules.Resolve(Registry(InstanceId.Parse("absent")), null, panel));

	[Fact]
	public void Недоступный_реестр_не_сбивает_выбор()
	{
		var unavailable = new RegistrySnapshot.Unavailable(Phrase.From("нет файла"));

		Assert.Equal(second, RegistryRules.Resolve(unavailable, second, panel));
		Assert.Equal(panel, RegistryRules.Resolve(unavailable, null, panel));
	}

	[Fact]
	public void Единственный_сервер_убрать_нельзя()
	{
		Assert.Equal(FailureCode.Conflict, RegistryRules.CanRemove([main], main).Failure!.Code);
		Assert.Equal(FailureCode.NotFound, RegistryRules.CanRemove([main, second], panel).Failure!.Code);
		Assert.True(RegistryRules.CanRemove([main, second], second).IsSuccess);
	}

	[Fact]
	public void Умолчание_после_удаления()
	{
		Assert.Equal(main, RegistryRules.DefaultAfterRemoving([main, second], main, second));
		Assert.Equal(panel, RegistryRules.DefaultAfterRemoving([main, second, panel], main, main));
	}

	[Theory]
	[InlineData("Сервер выживания", true)]
	[InlineData("  с пробелами  ", true)]
	[InlineData("", false)]
	[InlineData("a\nb", false)]
	public void Имя_сервера(string raw, bool accepted) =>
		Assert.Equal(accepted, InstanceName.Create(raw).IsSuccess);

	[Fact]
	public void Длинное_имя_сервера_отвергается() =>
		Assert.False(InstanceName.Create(new string('я', 65)).IsSuccess);

	private static RegistrySnapshot.Available Registry(InstanceId declared) => new(
		[Server(main), Server(second)],
		declared,
		RegistryStamp.Absent);

	private static RegisteredServer Server(InstanceId id) => new()
	{
		Id = id,
		Name = id.Value,
		Unit = $"vs-{id.Value}.service",
	};
}
