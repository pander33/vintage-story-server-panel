using Panel.Access.Domain;
using Panel.Access.Passwords;

namespace Panel.Access.Tests;

public sealed class AccountDirectoryTests
{
	private static readonly DateTimeOffset now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

	private static readonly PasswordHash password = PasswordHash.Create("пароль достаточной длины").Value;

	[Fact]
	public void Верные_учётные_данные_принимаются()
	{
		var result = Directory().Authenticate(
			UserName.Parse("admin"), new Credentials("пароль достаточной длины"), now);

		Assert.True(result.IsSuccess);
		Assert.Equal(Role.Admin, result.Value.Role);
	}

	/// <summary>
	/// Отказ один на все причины: иначе по нему можно узнать, существует ли учётная запись.
	/// </summary>
	[Theory]
	[InlineData("admin", "не тот пароль")]
	[InlineData("призрак", "пароль достаточной длины")]
	public void Неверные_учётные_данные_дают_один_и_тот_же_отказ(string user, string password)
	{
		var result = Directory().Authenticate(UserName.Parse(user), new Credentials(password), now);

		Assert.False(result.IsSuccess);
		Assert.Equal("Неверные учётные данные", result.Failure!.Message);
	}

	[Fact]
	public void Включённый_второй_фактор_обязателен()
	{
		var secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
		var directory = new AccountDirectory([
			Account("admin", Role.Admin) with { TotpEnabled = true, TotpSecret = secret },
		]);

		var withoutCode = directory.Authenticate(
			UserName.Parse("admin"), new Credentials("пароль достаточной длины"), now);
		var withCode = directory.Authenticate(
			UserName.Parse("admin"),
			new Credentials("пароль достаточной длины", OneTimeCode.Create(Totp.Code(secret, now)).Value),
			now);

		Assert.False(withoutCode.IsSuccess);
		Assert.True(withCode.IsSuccess);
	}

	/// <summary>
	/// Сессии отзываются только через поколение, поэтому смена пароля должна его увеличить.
	/// </summary>
	[Fact]
	public void Смена_пароля_увеличивает_поколение()
	{
		var directory = Directory();
		var before = directory.Find(UserName.Parse("admin"))!.Generation;

		var after = directory.Put(UserName.Parse("admin"), password, Role.Admin).Value;

		Assert.Equal(before + 1, after.Find(UserName.Parse("admin"))!.Generation);
		Assert.False(after.Validate(UserName.Parse("admin"), before).IsSuccess);
		Assert.True(after.Validate(UserName.Parse("admin"), before + 1).IsSuccess);
	}

	[Fact]
	public void Роль_берётся_из_учётной_записи_а_не_из_сессии()
	{
		var directory = Directory().Put(UserName.Parse("гость"), password, Role.Operator).Value;
		var downgraded = directory.Put(UserName.Parse("гость"), password, Role.Viewer).Value;

		var validated = downgraded.Validate(UserName.Parse("гость"), downgraded.Find(UserName.Parse("гость"))!.Generation);

		Assert.True(validated.IsSuccess);
		Assert.Equal(Role.Viewer, validated.Value.Role);
	}

	/// <summary>
	/// Сессии и API-токены удалённой записи хранят имя и поколение. Запись с тем же именем не
	/// должна начинать с прежнего поколения, иначе они снова действовали бы.
	/// </summary>
	[Fact]
	public void Пересозданная_запись_не_принимает_прежнее_поколение()
	{
		var bob = UserName.Parse("bob");
		var first = Directory().Put(bob, password, Role.Operator).Value;
		var generation = first.Find(bob)!.Generation;

		var recreated = first.Delete(bob).Value.Put(bob, password, Role.Operator).Value;

		Assert.False(recreated.Validate(bob, generation).IsSuccess);
		Assert.NotEqual(1, generation);
	}

	[Fact]
	public void Учётную_запись_admin_нельзя_понизить() =>
		Assert.Equal(
			"Основная учётная запись admin должна оставаться администратором",
			Directory().Put(UserName.Administrator, password, Role.Operator).Failure!.Message);

	[Fact]
	public void Учётную_запись_admin_нельзя_удалить() =>
		Assert.Equal(
			"Основную учётную запись admin удалить нельзя",
			Directory().Delete(UserName.Administrator).Failure!.Message);

	[Fact]
	public void Удаление_несуществующей_записи_даёт_NotFound() =>
		Assert.Equal(
			SharedKernel.FailureCode.NotFound,
			Directory().Delete(UserName.Parse("призрак")).Failure!.Code);

	/// <summary>
	/// Неудачная настройка второго фактора не выключает действующий: ожидающий секрет хранится
	/// отдельно.
	/// </summary>
	[Fact]
	public void Ожидающий_секрет_не_становится_действующим_без_подтверждения()
	{
		var begun = Directory().BeginTotp(UserName.Administrator).Value;
		var account = begun.Directory.Find(UserName.Administrator)!;

		Assert.False(account.TotpEnabled);
		Assert.Equal(string.Empty, account.TotpSecret);
		Assert.Equal(begun.Secret, account.PendingTotpSecret);
	}

	[Fact]
	public void Подтверждение_верным_кодом_включает_второй_фактор()
	{
		var begun = Directory().BeginTotp(UserName.Administrator).Value;
		var code = OneTimeCode.Create(Totp.Code(begun.Secret, now)).Value;

		var confirmed = begun.Directory.ConfirmTotp(UserName.Administrator, code, now).Value;
		var account = confirmed.Find(UserName.Administrator)!;

		Assert.True(account.TotpEnabled);
		Assert.Equal(begun.Secret, account.TotpSecret);
		Assert.Equal(string.Empty, account.PendingTotpSecret);
	}

	[Fact]
	public void Подтверждение_неверным_кодом_отвергается()
	{
		var begun = Directory().BeginTotp(UserName.Administrator).Value;

		var result = begun.Directory.ConfirmTotp(
			UserName.Administrator, OneTimeCode.Create("000000").Value, now);

		// Случайный секрет даст 000000 с вероятностью один на миллион; тогда тест упадёт
		// ложно, и это допустимо.
		Assert.False(result.IsSuccess);
		Assert.Equal("Неверный одноразовый код", result.Failure!.Message);
	}

	[Fact]
	public void Выключение_второго_фактора_стирает_оба_секрета()
	{
		var begun = Directory().BeginTotp(UserName.Administrator).Value;
		var disabled = begun.Directory.DisableTotp(UserName.Administrator).Value;
		var account = disabled.Find(UserName.Administrator)!;

		Assert.False(account.TotpEnabled);
		Assert.Equal(string.Empty, account.TotpSecret);
		Assert.Equal(string.Empty, account.PendingTotpSecret);
	}

	private static AccountDirectory Directory() => new([Account("admin", Role.Admin)]);

	private static Account Account(string name, Role role) => new()
	{
		Name = UserName.Parse(name),
		Password = password,
		Role = role,
		Generation = 3,
	};
}
