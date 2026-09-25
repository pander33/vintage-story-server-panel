using Panel.Access.Authorization;
using Panel.Access.Domain;
using Panel.Access.Sessions;
using Panel.SharedKernel;

namespace Panel.Access.Tests;

public sealed class AuthorizationTests
{
	[Requires(Permission.ObserveServer)]
	private sealed record Observe : ICommand;

	[Requires(Permission.ControlService)]
	private sealed record Control : ICommand;

	[Requires(Permission.Administer)]
	private sealed record Administer : ICommand;

	private sealed record WithoutAttribute : ICommand;

	[Theory]
	[InlineData(Role.Viewer, true)]
	[InlineData(Role.Operator, true)]
	[InlineData(Role.Admin, true)]
	public void Наблюдать_состояние_разрешено_всем(Role role, bool allowed) =>
		Assert.Equal(allowed, new CommandAuthorizer().Authorize<Observe>(Identity(role)).IsSuccess);

	[Theory]
	[InlineData(Role.Viewer, false)]
	[InlineData(Role.Operator, true)]
	[InlineData(Role.Admin, true)]
	public void Управление_службой_запрещено_наблюдателю(Role role, bool allowed) =>
		Assert.Equal(allowed, new CommandAuthorizer().Authorize<Control>(Identity(role)).IsSuccess);

	[Theory]
	[InlineData(Role.Viewer, false)]
	[InlineData(Role.Operator, false)]
	[InlineData(Role.Admin, true)]
	public void Административные_действия_только_администратору(Role role, bool allowed) =>
		Assert.Equal(allowed, new CommandAuthorizer().Authorize<Administer>(Identity(role)).IsSuccess);

	[Requires(Permission.SendRawConsole)]
	private sealed record RawConsole : ICommand;

	/// <summary>ADR-17: оператору доступен каталог, произвольная консоль только администратору.</summary>
	[Theory]
	[InlineData(Role.Viewer, false)]
	[InlineData(Role.Operator, false)]
	[InlineData(Role.Admin, true)]
	public void Произвольная_консоль_только_администратору(Role role, bool allowed) =>
		Assert.Equal(allowed, new CommandAuthorizer().Authorize<RawConsole>(Identity(role)).IsSuccess);

	[Fact]
	public void Наблюдатель_никогда_не_видит_адресов()
	{
		Assert.True(Identity(Role.Viewer).HidesAddresses);
		Assert.False(Identity(Role.Operator).HidesAddresses);
		Assert.False(Identity(Role.Admin).HidesAddresses);
	}

	[Fact]
	public void Тексты_отказов_совпадают_с_панелью_1_2()
	{
		var viewer = new CommandAuthorizer().Authorize<Control>(Identity(Role.Viewer));
		var operatorRole = new CommandAuthorizer().Authorize<Administer>(Identity(Role.Operator));

		Assert.Equal("Роль наблюдателя не разрешает изменения", viewer.Failure!.Message);
		Assert.Equal("Для этого действия требуется роль администратора", operatorRole.Failure!.Message);
	}

	/// <summary>
	/// Команда без объявленного права не выполняется: разрешения по умолчанию нет.
	/// </summary>
	[Fact]
	public void Команда_без_атрибута_валит_проверку() =>
		Assert.Throws<InvalidOperationException>(
			() => new CommandAuthorizer().Authorize<WithoutAttribute>(Identity(Role.Admin)));

	private static Identity Identity(Role role)
	{
		var payload = SessionFixture.Token(role);
		var reader = new SessionReader(SessionSecret.Create(SessionFixture.Secret).Value);
		var identity = reader.Read(payload);
		Assert.True(identity.IsSuccess, identity.Failure?.Message);
		return identity.Value;
	}
}
