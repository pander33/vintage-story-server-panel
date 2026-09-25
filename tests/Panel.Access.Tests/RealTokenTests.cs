using Panel.Access.Sessions;

namespace Panel.Access.Tests;

/// <summary>
/// Сессия, выданная на тестовом сервере, принимается панелью.
/// </summary>
/// <remarks>
/// Без фикстур тест ничего не проверяет. Фикстуры снимаются с тестового сервера по заметке
/// «Контракт HTTP-API и снятие золотых фикстур».
///
/// Файлы не коммитятся: fixtures/live/ в .gitignore. secret.key подписывает все сессии
/// панели, а токен действует до истечения срока.
/// </remarks>
public sealed class RealTokenTests
{
	private static readonly string directory = Path.Combine(
		AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "live");

	[Fact]
	public void Сессия_выданная_панелью_1_2_принимается()
	{
		var keyPath = Path.Combine(directory, "secret.key");
		var tokenPath = Path.Combine(directory, "session-token.txt");
		if (!File.Exists(keyPath) || !File.Exists(tokenPath))
		{
			// Фикстур нет: проверка выполняется на сервере.
			return;
		}

		var secret = SessionSecret.Create(File.ReadAllBytes(keyPath));
		Assert.True(secret.IsSuccess, secret.Failure?.Message);

		var identity = new SessionReader(secret.Value).Read(File.ReadAllText(tokenPath).Trim());

		Assert.True(identity.IsSuccess, identity.Failure?.Message);
	}
}
