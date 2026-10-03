using System.Text.Json.Nodes;
using Panel.Access.Authorization;
using Panel.Access.Domain;
using Panel.Access.Infrastructure;
using Panel.Access.Passwords;
using Panel.Access.Sessions;
using Panel.SharedKernel;

namespace Panel.Access.Tests;

/// <summary>
/// API-токены (findings §82): выпуск не выше роли владельца, проверка секрета и срока, отзыв
/// сменой пароля или роли, хранение одного хеша.
/// </summary>
public sealed class ApiTokenTests : IDisposable
{
	private static readonly DateTimeOffset now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

	private static readonly PasswordHash password = PasswordHash.Create("пароль достаточной длины").Value;

	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-tokens-" + Guid.NewGuid().ToString("n"));

	public ApiTokenTests() => Directory.CreateDirectory(directory);

	public void Dispose() => Directory.Delete(directory, recursive: true);

	[Fact]
	public void Токен_проверяется_по_секрету_и_хранит_только_хеш()
	{
		var issued = Issue(Accounts(), "alice", "grafana", Role.Viewer);

		Assert.StartsWith(PresentedApiToken.Prefix, issued.Plain, StringComparison.Ordinal);
		Assert.DoesNotContain(issued.Plain[(PresentedApiToken.Prefix.Length + ApiTokenId.Length + 1)..], issued.Token.Hash, StringComparison.Ordinal);
		Assert.True(issued.Tokens.Authenticate(PresentedApiToken.Parse(issued.Plain).Value, now).IsSuccess);

		// Тот же идентификатор с другим секретом — отказ.
		var forged = issued.Plain[..^4] + (issued.Plain.EndsWith("AAAA", StringComparison.Ordinal) ? "BBBB" : "AAAA");
		Assert.False(issued.Tokens.Authenticate(PresentedApiToken.Parse(forged).Value, now).IsSuccess);
	}

	[Theory]
	[InlineData("")]
	[InlineData("vsp_")]
	[InlineData("vsp_0123456789abcdef")]
	[InlineData("vsp_0123456789ABCDEF_secret")]
	[InlineData("ghp_0123456789abcdef_secret")]
	[InlineData("vsp_0123456789abcdef-secret")]
	public void Строка_не_того_вида_не_токен(string raw) =>
		Assert.Equal(FailureCode.Unauthenticated, PresentedApiToken.Parse(raw).Failure!.Code);

	[Fact]
	public void Роль_токена_не_выше_роли_владельца()
	{
		var accounts = Accounts();
		var owner = accounts.Authenticate(UserName.Parse("bob"), new Credentials("пароль достаточной длины"), now).Value;

		var refused = ApiTokenSet.Empty.Issue(owner, Draft("too much", Role.Admin), now);

		Assert.Equal(FailureCode.Forbidden, refused.Failure!.Code);
		Assert.True(ApiTokenSet.Empty.Issue(owner, Draft("same", Role.Operator), now).IsSuccess);
	}

	[Fact]
	public void Истёкший_токен_не_действует()
	{
		var accounts = Accounts();
		var owner = accounts.Authenticate(UserName.Parse("alice"), new Credentials("пароль достаточной длины"), now).Value;
		var issued = ApiTokenSet.Empty.Issue(owner, Draft("short", Role.Viewer, TimeSpan.FromDays(1)), now).Value;
		var presented = PresentedApiToken.Parse(issued.Plain).Value;

		Assert.True(issued.Tokens.Authenticate(presented, now.AddHours(23)).IsSuccess);
		Assert.False(issued.Tokens.Authenticate(presented, now.AddDays(1)).IsSuccess);
		Assert.Empty(issued.Tokens.Prune(accounts, now.AddDays(2)).All);
	}

	[Fact]
	public void Чужой_токен_отзывает_только_администратор()
	{
		var issued = Issue(Accounts(), "alice", "grafana", Role.Viewer);

		Assert.Equal(FailureCode.NotFound, issued.Tokens.Revoke(issued.Token.Id, UserName.Parse("bob"), administrator: false).Failure!.Code);
		Assert.Empty(issued.Tokens.Revoke(issued.Token.Id, UserName.Parse("admin"), administrator: true).Value.All);
		Assert.Empty(issued.Tokens.Revoke(issued.Token.Id, UserName.Parse("alice"), administrator: false).Value.All);
	}

	[Fact]
	public void Не_больше_двадцати_токенов_на_учётную_запись()
	{
		var accounts = Accounts();
		var owner = accounts.Authenticate(UserName.Parse("alice"), new Credentials("пароль достаточной длины"), now).Value;
		var tokens = ApiTokenSet.Empty;
		for (var index = 0; index < ApiTokenSet.MaxPerUser; index++)
		{
			tokens = tokens.Issue(owner, Draft($"t{index}", Role.Viewer), now).Value.Tokens;
		}

		Assert.Equal(FailureCode.Conflict, tokens.Issue(owner, Draft("one more", Role.Viewer), now).Failure!.Code);
	}

	[Fact]
	public async Task Хранилище_сохраняет_и_читает_токены()
	{
		var store = new FileApiTokenStore(Path.Combine(directory, "api-tokens.json"));
		var issued = Issue(Accounts(), "bob", "grafana", Role.Operator);

		var written = await store.ChangeAsync(_ => Result.Ok((issued.Tokens, true)), CancellationToken.None);
		var read = await store.LoadAsync(CancellationToken.None);

		Assert.True(written.IsSuccess);
		var token = Assert.Single(read.Value.All);
		Assert.Equal(issued.Token, token);
		Assert.DoesNotContain(issued.Plain, await File.ReadAllTextAsync(Path.Combine(directory, "api-tokens.json")), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Испорченный_файл_токенов_отказ_а_не_пустой_набор()
	{
		var path = Path.Combine(directory, "api-tokens.json");
		await File.WriteAllTextAsync(path, "{ не json");

		Assert.Equal(FailureCode.Unavailable, (await new FileApiTokenStore(path).LoadAsync(CancellationToken.None)).Failure!.Code);
	}

	[Fact]
	public void Выпуск_по_старому_удостоверению_сохраняет_новые_токены_других_поколений()
	{
		var old = Accounts();
		var changed = old.Put(UserName.Parse("bob"), password, Role.Operator).Value;
		var fresh = Issue(changed, "bob", "new", Role.Operator);
		var oldOwner = old.Authenticate(UserName.Parse("alice"), new Credentials("пароль достаточной длины"), now).Value;

		// Запрос alice начался раньше смены поколения bob, но записывает набор после выпуска new.
		var issued = fresh.Tokens.Issue(oldOwner, Draft("alice", Role.Viewer), now).Value;

		Assert.Contains(fresh.Token, issued.Tokens.All);
		Assert.True(issued.Tokens.Authenticate(PresentedApiToken.Parse(fresh.Plain).Value, now).IsSuccess);
	}

	[Fact]
	public void Предел_не_считает_отозванное_поколение_и_истёкшие_токены()
	{
		var old = Accounts();
		var owner = old.Authenticate(UserName.Parse("bob"), new Credentials("пароль достаточной длины"), now).Value;
		var tokens = ApiTokenSet.Empty;
		for (var index = 0; index < ApiTokenSet.MaxPerUser; index++)
		{
			tokens = tokens.Issue(owner, Draft($"old{index}", Role.Operator), now).Value.Tokens;
		}

		var changed = old.Put(owner.Name, password, Role.Operator).Value;
		var freshOwner = changed.Authenticate(owner.Name, new Credentials("пароль достаточной длины"), now).Value;
		var fresh = tokens.Issue(freshOwner, Draft("fresh", Role.Operator, TimeSpan.FromDays(1)), now).Value;
		Assert.Single(fresh.Tokens.Prune(changed, now).All);

		var later = fresh.Tokens.Issue(freshOwner, Draft("later", Role.Operator), now.AddDays(2)).Value;
		Assert.DoesNotContain(fresh.Token, later.Tokens.All);
		Assert.Single(later.Tokens.Prune(changed, now.AddDays(2)).All);
	}

	[Theory]
	[InlineData("expires_at", "\"bad timestamp\"")]
	[InlineData("expires_at", "true")]
	[InlineData("expires_at", "{}")]
	[InlineData("expires_at", "[]")]
	[InlineData("expires_at", "1.5")]
	[InlineData("expires_at", "9223372036854775807")]
	[InlineData("expires_at", "-62135596801")]
	[InlineData("expires_at", "253402300800")]
	[InlineData("created_at", "null")]
	[InlineData("created_at", "\"bad timestamp\"")]
	[InlineData("created_at", "9223372036854775807")]
	[InlineData("created_at", "-9223372036854775808")]
	[InlineData("created_at", "-62135596801")]
	[InlineData("created_at", "253402300800")]
	public async Task Неверная_дата_исключает_запись_без_потери_остальных(string field, string raw)
	{
		var path = Path.Combine(directory, "api-tokens.json");
		var first = Issue(Accounts(), "alice", "broken", Role.Viewer);
		var owner = Accounts().Authenticate(UserName.Parse("bob"), new Credentials("пароль достаточной длины"), now).Value;
		var both = first.Tokens.Issue(owner, Draft("valid", Role.Operator), now).Value;
		await new FileApiTokenStore(path).ChangeAsync(_ => Result.Ok((both.Tokens, true)), default);
		var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
		document["tokens"]![0]![field] = JsonNode.Parse(raw);
		await File.WriteAllTextAsync(path, document.ToJsonString());

		var loaded = await new FileApiTokenStore(path).LoadAsync(default);

		Assert.True(loaded.IsSuccess);
		Assert.Equal(both.Token, Assert.Single(loaded.Value.All));
		Assert.False(loaded.Value.Authenticate(PresentedApiToken.Parse(first.Plain).Value, now).IsSuccess);
		Assert.True(loaded.Value.Authenticate(PresentedApiToken.Parse(both.Plain).Value, now).IsSuccess);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("null")]
	[InlineData("253402300799")]
	public async Task Отсутствующий_срок_и_предельные_даты_читаются(string? expires)
	{
		var path = Path.Combine(directory, "api-tokens.json");
		var issued = Issue(Accounts(), "alice", "valid", Role.Viewer);
		await new FileApiTokenStore(path).ChangeAsync(_ => Result.Ok((issued.Tokens, true)), default);
		var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
		var entry = document["tokens"]![0]!.AsObject();
		entry["created_at"] = DateTimeOffset.MinValue.ToUnixTimeSeconds();
		if (expires is null)
		{
			entry.Remove("expires_at");
		}
		else
		{
			entry["expires_at"] = JsonNode.Parse(expires);
		}
		await File.WriteAllTextAsync(path, document.ToJsonString());

		var loaded = await new FileApiTokenStore(path).LoadAsync(default);

		Assert.True(loaded.IsSuccess);
		Assert.True(loaded.Value.Authenticate(PresentedApiToken.Parse(issued.Plain).Value, now).IsSuccess);
		Assert.Equal(DateTimeOffset.MinValue, Assert.Single(loaded.Value.All).CreatedAt);
		Assert.Equal(expires == "253402300799" ? DateTimeOffset.FromUnixTimeSeconds(253402300799) : (DateTimeOffset?) null,
			loaded.Value.All[0].ExpiresAt);
	}

	/// <summary>
	/// Роль удостоверения — меньшая из роли токена и текущей роли владельца; новое поколение
	/// (пароль, роль) отзывает токен, как и сессию.
	/// </summary>
	[Fact]
	public async Task Удостоверение_по_токену_следует_за_учётной_записью()
	{
		var accounts = new FileAccountStore(Path.Combine(directory, "accounts.json"), string.Empty);
		var tokens = new FileApiTokenStore(Path.Combine(directory, "api-tokens.json"));
		await accounts.SaveAsync(Accounts(), CancellationToken.None);
		var issued = Issue(Accounts(), "bob", "deploy", Role.Operator);
		await tokens.ChangeAsync(_ => Result.Ok((issued.Tokens, true)), CancellationToken.None);
		var guard = new ApiTokenGuard(tokens, accounts, new FixedTime(now));

		var identity = (await guard.AuthenticateAsync(issued.Plain, CancellationToken.None)).Value;
		Assert.Equal(Role.Operator, identity.Role);
		Assert.Equal("deploy", identity.ApiToken);
		Assert.True(identity.ViaApiToken);

		// Понижение роли — новое поколение: токен отозван.
		var lowered = Accounts().Put(UserName.Parse("bob"), password, Role.Viewer).Value;
		await accounts.SaveAsync(lowered, CancellationToken.None);
		Assert.Equal(FailureCode.Unauthenticated, (await guard.AuthenticateAsync(issued.Plain, CancellationToken.None)).Failure!.Code);
	}

	[RequiresSession]
	[Requires(Permission.ObserveServer)]
	private sealed record SessionOnly : ICommand;

	[Requires(Permission.ObserveServer)]
	private sealed record Observe : ICommand;

	[Fact]
	public async Task Токен_не_выполняет_команды_только_для_сессии()
	{
		var accounts = new FileAccountStore(Path.Combine(directory, "accounts.json"), string.Empty);
		var tokens = new FileApiTokenStore(Path.Combine(directory, "api-tokens.json"));
		await accounts.SaveAsync(Accounts(), CancellationToken.None);
		var issued = Issue(Accounts(), "admin", "ci", Role.Admin);
		await tokens.ChangeAsync(_ => Result.Ok((issued.Tokens, true)), CancellationToken.None);
		var identity = (await new ApiTokenGuard(tokens, accounts, new FixedTime(now)).AuthenticateAsync(issued.Plain, CancellationToken.None)).Value;

		var authorizer = new CommandAuthorizer();
		Assert.True(authorizer.Authorize<Observe>(identity).IsSuccess);
		Assert.Equal(FailureCode.Forbidden, authorizer.Authorize<SessionOnly>(identity).Failure!.Code);
	}

	private static IssuedApiToken Issue(AccountDirectory accounts, string user, string name, Role role)
	{
		var owner = accounts.Authenticate(UserName.Parse(user), new Credentials("пароль достаточной длины"), now).Value;
		return ApiTokenSet.Empty.Issue(owner, Draft(name, role), now).Value;
	}

	private static ApiTokenDraft Draft(string name, Role role, TimeSpan? lifetime = null) =>
		new(ApiTokenName.Create(name).Value, role, lifetime);

	private static AccountDirectory Accounts() => new([
		new Account { Name = UserName.Parse("admin"), Password = password, Role = Role.Admin, Generation = 1 },
		new Account { Name = UserName.Parse("alice"), Password = password, Role = Role.Viewer, Generation = 1 },
		new Account { Name = UserName.Parse("bob"), Password = password, Role = Role.Operator, Generation = 1 },
	]);

	private sealed class FixedTime(DateTimeOffset moment) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => moment;
	}
}
