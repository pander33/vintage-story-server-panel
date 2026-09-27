using System.Net;
using Panel.Cli;
using Panel.Provisioning.Domain;

namespace Panel.Cli.Tests;

/// <summary>Доступ к панели, заданный при установке: параметры, строки <c>vs-panel.env</c> и сводка.</summary>
public sealed class PanelAccessTests
{
	private static readonly Dictionary<string, string> none = new(StringComparer.Ordinal);

	private static IReadOnlyDictionary<string, string> Parsed(IReadOnlyList<string> arguments, Dictionary<string, string>? environment = null) =>
		InstallOptions.Parse(arguments, environment ?? none, InstallPaths.System).Value.PanelEnvironment;

	[Fact]
	public void Без_параметров_файл_окружения_не_меняется()
	{
		Assert.Empty(Parsed([]));
	}

	[Theory]
	[InlineData("localhost", "127.0.0.1", null)]
	[InlineData("lan", "0.0.0.0", "lan")]
	[InlineData("internet", "0.0.0.0", "internet")]
	public void Готовый_набор_доступа(string access, string bind, string? firewall)
	{
		var values = Parsed(["--access", access]);

		Assert.Equal(bind, values["PANEL_BIND"]);
		Assert.Equal(firewall, values.GetValueOrDefault(PanelListener.FirewallKey));
	}

	/// <summary>Панель, открытая сети, отвечает и по адресу машины: иначе фильтр Host вернёт 400.</summary>
	[Theory]
	[InlineData("lan")]
	[InlineData("internet")]
	public void Сетевой_набор_разрешает_любое_имя(string access)
	{
		Assert.Equal("*", Parsed(["--access", access])["PANEL_ALLOWED_HOSTS"]);
		Assert.Equal("panel.example.org", Parsed(["--allowed-hosts", "panel.example.org", "--access", access])["PANEL_ALLOWED_HOSTS"]);
		Assert.False(Parsed(["--access", "localhost"]).ContainsKey("PANEL_ALLOWED_HOSTS"));
	}

	[Fact]
	public void Отдельный_параметр_уточняет_набор_в_любом_порядке()
	{
		var values = Parsed(["--port", "9000", "--access", "internet", "--bind", "192.168.1.10", "--secure-cookie", "true"]);

		Assert.Equal("192.168.1.10", values["PANEL_BIND"]);
		Assert.Equal("9000", values["PANEL_PORT"]);
		Assert.Equal("internet", values[PanelListener.FirewallKey]);
		Assert.Equal("true", values["PANEL_SECURE_COOKIE"]);
	}

	[Fact]
	public void Переменные_окружения_как_у_install_sh_а_параметр_важнее()
	{
		var values = Parsed(
			["--port", "9100"],
			new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["VS_PANEL_ACCESS"] = "internet",
				["VS_PANEL_PORT"] = "9000",
				["VS_PANEL_ALLOWED_HOSTS"] = "panel.example.org,*.lan",
			});

		Assert.Equal("0.0.0.0", values["PANEL_BIND"]);
		Assert.Equal("9100", values["PANEL_PORT"]);
		Assert.Equal("panel.example.org,*.lan", values["PANEL_ALLOWED_HOSTS"]);
	}

	[Theory]
	[InlineData("--access", "world")]
	[InlineData("--firewall", "all")]
	[InlineData("--port", "0")]
	[InlineData("--port", "70000")]
	[InlineData("--bind", "example.org")]
	[InlineData("--allowed-hosts", "a*b")]
	[InlineData("--secure-cookie", "yes")]
	public void Неверное_значение_доступа_отвергается_до_установки(string argument, string value)
	{
		Assert.False(InstallOptions.Parse([argument, value], none, InstallPaths.System).IsSuccess);
	}

	[Fact]
	public void Параметр_без_значения_отвергается()
	{
		Assert.False(InstallOptions.Parse(["--port"], none, InstallPaths.System).IsSuccess);
		Assert.False(InstallOptions.ParseWindows(["--bind", "--no-server"], none).IsSuccess);
	}

	[Fact]
	public void Строка_заменяется_на_месте_а_новая_дописывается()
	{
		const string text = "PANEL_BIND=127.0.0.1\n# комментарий\nPANEL_PORT=8080\nPANEL_STATE_DIR=/var/lib/vs-panel\n";

		var written = PanelAccess.Rewrite(text, new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["PANEL_BIND"] = "0.0.0.0",
			[PanelListener.FirewallKey] = "internet",
		});

		Assert.Equal("PANEL_BIND=0.0.0.0\n# комментарий\nPANEL_PORT=8080\nPANEL_STATE_DIR=/var/lib/vs-panel\nPANEL_FIREWALL=internet\n", written);
	}

	[Fact]
	public void Без_изменений_файл_тот_же()
	{
		var template = Templates.PanelEnvironment;

		Assert.Equal(template, PanelAccess.Rewrite(template, none));
	}

	/// <summary>С коробки панель видна в локальной сети, но не в интернете.</summary>
	[Fact]
	public void Шаблон_Linux_открывает_панель_локальной_сети()
	{
		var environment = EnvironmentFile.Parse(Templates.PanelEnvironment);
		var listener = PanelListener.FromEnvironment(environment);

		Assert.Equal("0.0.0.0", listener.Bind);
		Assert.Equal(FirewallScope.Lan, listener.Firewall);
		Assert.Equal("*", environment["PANEL_ALLOWED_HOSTS"]);
		Assert.Equal("false", environment["PANEL_SECURE_COOKIE"]);
	}

	[Fact]
	public void Сводка_называет_адреса_в_сети_и_предупреждает_об_HTTP()
	{
		var output = new StringWriter();
		var environment = EnvironmentFile.Parse("PANEL_BIND=0.0.0.0\nPANEL_PORT=8080\nPANEL_SECURE_COOKIE=false\nPANEL_FIREWALL=internet\n");

		PanelAccess.Print(output, environment, [IPAddress.Parse("192.168.1.10")], tunnel: true);

		var text = output.ToString();
		Assert.Contains("http://127.0.0.1:8080/", text, StringComparison.Ordinal);
		Assert.Contains("из сети: http://192.168.1.10:8080/", text, StringComparison.Ordinal);
		Assert.Contains("пробросьте", text, StringComparison.Ordinal);
		Assert.Contains("открытым текстом", text, StringComparison.Ordinal);
		Assert.DoesNotContain("ssh -L", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Сводка_для_петли_подсказывает_туннель()
	{
		var output = new StringWriter();

		PanelAccess.Print(output, EnvironmentFile.Parse("PANEL_BIND=127.0.0.1\nPANEL_PORT=8080\n"), [IPAddress.Parse("192.168.1.10")], tunnel: true);

		Assert.Contains("ssh -L 8080:127.0.0.1:8080", output.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("192.168.1.10", output.ToString(), StringComparison.Ordinal);
	}
}
