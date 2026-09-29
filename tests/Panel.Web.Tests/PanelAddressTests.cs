using Panel.Web.Application;

namespace Panel.Web.Tests;

/// <summary>Ссылка «Перейти в панель» в конце мастера установки.</summary>
public sealed class PanelAddressTests
{
	/// <summary>Панель на другой машине: по <c>0.0.0.0</c> браузер её не найдёт.</summary>
	[Theory]
	[InlineData("0.0.0.0")]
	[InlineData("::")]
	public void Все_интерфейсы_дают_хост_браузера(string bind)
	{
		Assert.Equal("http://192.168.1.20:8080/", PanelAddress.After(bind, "8080", "192.168.1.20"));
	}

	[Fact]
	public void Смена_порта_сохраняет_хост_браузера()
	{
		Assert.Equal("http://vs.example.org:9000/", PanelAddress.After("0.0.0.0", "9000", "vs.example.org"));
	}

	[Fact]
	public void Без_хоста_запроса_петля()
	{
		Assert.Equal("http://127.0.0.1:8080/", PanelAddress.After("0.0.0.0", "8080", null));
	}

	[Fact]
	public void Конкретный_адрес_остаётся()
	{
		Assert.Equal("http://10.0.0.5:8080/", PanelAddress.After("10.0.0.5", "8080", "192.168.1.20"));
	}

	[Fact]
	public void Адрес_IPv6_в_скобках()
	{
		Assert.Equal("http://[fd00::5]:8080/", PanelAddress.After("fd00::5", "8080", "[fd00::1]"));
		Assert.Equal("http://[fd00::1]:8080/", PanelAddress.After("::", "8080", "[fd00::1]"));
	}
}
