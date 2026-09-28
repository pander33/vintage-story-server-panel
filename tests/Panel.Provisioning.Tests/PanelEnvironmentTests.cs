using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Provisioning.Tests;

/// <summary>Шаг «Доступ», файл окружения панели и умолчания нового сервера.</summary>
public sealed class PanelEnvironmentTests
{
	[Fact]
	public void Менять_можно_ровно_шесть_ключей()
	{
		Assert.Equal(
			["PANEL_ALLOWED_HOSTS", "PANEL_BIND", "PANEL_GAME_VERSION", "PANEL_INSTANCE", "PANEL_PORT", "PANEL_SECURE_COOKIE"],
			PanelSetting.Keys.Order(StringComparer.Ordinal));
	}

	[Theory]
	[InlineData("PANEL_STATE_DIR", "/tmp")]
	[InlineData("PANEL_AGENT_SOCKET", "/tmp/x")]
	[InlineData("PANEL_BIND=0.0.0.0\nPANEL_STATE_DIR", "/tmp")]
	public void Граница_доверия_из_браузера_не_двигается(string key, string value)
	{
		Assert.Equal("This panel setting cannot be changed from the web interface", PanelSetting.Create(key, value).Failure!.Message);
	}

	[Theory]
	[InlineData("PANEL_BIND", "localhost")]
	[InlineData("PANEL_PORT", "80a")]
	[InlineData("PANEL_ALLOWED_HOSTS", "a b")]
	[InlineData("PANEL_SECURE_COOKIE", "yes")]
	[InlineData("PANEL_GAME_VERSION", "1.22")]
	[InlineData("PANEL_INSTANCE", "Main")]
	[InlineData("PANEL_INSTANCE", "main\nPANEL_STATE_DIR=/tmp")]
	public void Значение_проверяется_своим_выражением(string key, string value)
	{
		Assert.Equal($"Invalid value for {key}", PanelSetting.Create(key, value).Failure!.Message);
	}

	[Fact]
	public void Звёздочка_разрешает_любой_хост()
	{
		Assert.True(PanelSetting.Create("PANEL_ALLOWED_HOSTS", "*").IsSuccess);
		Assert.True(PanelSetting.Create("PANEL_ALLOWED_HOSTS", "localhost,127.0.0.1,*.local").IsSuccess);
	}

	/// <summary>HostFiltering понимает звёздочку только целиком или в начале имени; иначе сравнил бы её буквально.</summary>
	[Theory]
	[InlineData("a*b")]
	[InlineData("**")]
	[InlineData("*.")]
	[InlineData("*.a*.local")]
	[InlineData("localhost,panel*")]
	public void Звёздочка_в_середине_имени_отклоняется(string hosts)
	{
		Assert.False(PanelSetting.Create("PANEL_ALLOWED_HOSTS", hosts).IsSuccess);
	}

	[Fact]
	public void Строка_заменяется_а_отсутствующая_дописывается()
	{
		var lines = new[] { "# панель", "PANEL_PORT=8080", "PANEL_BIND=127.0.0.1" };

		Assert.Equal(
			"# панель\nPANEL_PORT=9000\nPANEL_BIND=127.0.0.1\n",
			PanelSetting.Create("PANEL_PORT", "9000").Value.Rewrite(lines));
		Assert.Equal(
			"# панель\nPANEL_PORT=8080\nPANEL_BIND=127.0.0.1\nPANEL_INSTANCE=creative\n",
			PanelSetting.Create("PANEL_INSTANCE", "creative").Value.Rewrite(lines));
	}

	[Fact]
	public void Настройки_доступа_становятся_ровно_четырьмя_ключами()
	{
		var access = AccessSettings.Create("0.0.0.0", 9000, " panel.example , 10.0.0.2 ", secureCookie: true).Value;

		Assert.Equal(
			[("PANEL_BIND", "0.0.0.0"), ("PANEL_PORT", "9000"), ("PANEL_ALLOWED_HOSTS", "panel.example,10.0.0.2"), ("PANEL_SECURE_COOKIE", "true")],
			access.Values);
		Assert.Equal("http://0.0.0.0:9000/", access.Url);
	}

	[Theory]
	[InlineData("127.0.0.1", false)]
	[InlineData("::1", false)]
	[InlineData(" localhost ", false)]
	[InlineData("0.0.0.0", true)]
	public void Только_петля_держит_панель_вне_сети(string bind, bool published)
	{
		Assert.Equal(published, AccessSettings.Create(bind, 8080, "localhost", secureCookie: false).Value.Published);
	}

	[Fact]
	public void Невозможная_настройка_доступа_отвергается_с_причиной()
	{
		Assert.Equal("Порт панели должен быть от 1 до 65535", AccessSettings.Create("127.0.0.1", 0, "localhost", false).Failure!.Message);
		Assert.Equal("Укажите хотя бы один разрешённый хост", AccessSettings.Create("127.0.0.1", 8080, " , ", false).Failure!.Message);
		Assert.Equal("Слишком длинный список разрешённых хостов", AccessSettings.Create("127.0.0.1", 8080, new string('a', 256), false).Failure!.Message);
	}

	[Fact]
	public void Подсказка_обходит_занятое()
	{
		Assert.Equal(("main", 42420), Suggestion.For([]));
		Assert.Equal(("server3", 42422), Suggestion.For([("main", 42420), ("server2", 42421)]));
	}

	[Fact]
	public void Места_нужно_вчетверо_больше_пакета_но_не_меньше_трёх_гигабайт()
	{
		const long gib = 1024L * 1024 * 1024;

		Assert.Equal(3 * gib, DiskRequirement.For(200L * 1024 * 1024));
		Assert.Equal(8 * gib, DiskRequirement.For(2 * gib));
		Assert.Equal("Недостаточно места: свободно 1 ГиБ, нужно около 3 ГиБ.", DiskRequirement.Blocker(gib, 3 * gib)?.ToString());
		Assert.Null(DiskRequirement.Blocker(0, 3 * gib));
		Assert.Null(DiskRequirement.Blocker(5 * gib, 3 * gib));
	}
}
