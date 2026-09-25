using Panel.Web;

namespace Panel.Web.Tests;

/// <summary>
/// На Windows <c>vs-panel.env</c> читает сама панель, потому что systemd там нет. Формат тот,
/// что пишут установщик и <c>panel-env-set</c>.
/// </summary>
public sealed class PanelOptionsTests : IDisposable
{
	private readonly string file = Path.Combine(Path.GetTempPath(), "vs-panel-" + Guid.NewGuid().ToString("n") + ".env");

	public void Dispose() => File.Delete(file);

	[Fact]
	public void Строки_ключ_значение_читаются()
	{
		File.WriteAllText(file, "PANEL_INSTANCE=creative\r\n# комментарий\nPANEL_PORT = 8090\n\nмусор без знака\n");

		var values = PanelOptions.ReadEnvironmentFile(file);

		Assert.Equal("creative", values["PANEL_INSTANCE"]);
		Assert.Equal("8090", values["PANEL_PORT"]);
		Assert.Equal(2, values.Count);
	}

	[Fact]
	public void Значение_со_знаком_равенства_не_режется()
	{
		File.WriteAllText(file, "PANEL_ALLOWED_HOSTS=a=b,localhost\n");

		Assert.Equal("a=b,localhost", PanelOptions.ReadEnvironmentFile(file)["PANEL_ALLOWED_HOSTS"]);
	}

	[Fact]
	public void Нет_файла_нет_значений()
	{
		Assert.Empty(PanelOptions.ReadEnvironmentFile(file));
	}
}
