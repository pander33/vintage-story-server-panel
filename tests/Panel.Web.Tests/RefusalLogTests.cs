using Panel.Web.Endpoints;

namespace Panel.Web.Tests;

/// <summary>
/// Какие отказы попадают в журнал панели: по нему и по диагностике разбирают сорвавшиеся действия.
/// </summary>
public sealed class RefusalLogTests
{
	[Theory]
	[InlineData("POST", 502)]
	[InlineData("POST", 409)]
	[InlineData("POST", 400)]
	[InlineData("DELETE", 404)]
	[InlineData("GET", 503)]
	public void Отказ_изменяющего_запроса_и_любой_5xx_пишутся(string method, int status) =>
		Assert.True(PanelResults.Logged(method, status));

	[Theory]
	[InlineData("POST", 401)]
	[InlineData("GET", 403)]
	[InlineData("GET", 404)]
	[InlineData("HEAD", 409)]
	[InlineData("POST", 200)]
	public void Вход_отказы_чтения_и_успех_не_пишутся(string method, int status) =>
		Assert.False(PanelResults.Logged(method, status));
}
