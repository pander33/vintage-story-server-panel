using System.Text.Json.Nodes;
using Panel.Runtime.Domain;
using Panel.Web.Application;

namespace Panel.Web.Tests;

/// <summary>Ответ <c>GET /api/players/lists</c>: приватность UID и режим белого списка.</summary>
public sealed class PlayerListsViewTests
{
	private static readonly PlayerLists lists = new(
		[new PlayerListEntry("Griefer", "abc=", "Admin", 1893456000, "Разрушение")],
		[new PlayerListEntry("Friend", "def=", null, null, null)]);

	[Fact]
	public void Наблюдатель_не_видит_UID()
	{
		var response = PlayerListsView.Compose(lists, [], hideUid: true);

		Assert.All(response.Banned.Concat(response.Whitelisted), entry => Assert.Equal(string.Empty, entry.Uid));
		Assert.Equal("Griefer", response.Banned[0].Player);
	}

	[Fact]
	public void С_правом_на_адреса_UID_виден()
	{
		var response = PlayerListsView.Compose(lists, [], hideUid: false);

		Assert.Equal("abc=", response.Banned[0].Uid);
		Assert.Equal(1893456000, response.Banned[0].Until);
		Assert.False(response.Banned[0].Forever);
		Assert.True(response.Whitelisted[0].Forever);
	}

	[Theory]
	[InlineData("""{"WhitelistMode": 2}""", true)]
	[InlineData("""{"WhitelistMode": "On"}""", true)]
	[InlineData("""{"WhitelistMode": 1}""", false)]
	[InlineData("""{"WhitelistMode": "Default"}""", false)]
	[InlineData("""{"OnlyWhitelisted": true}""", true)]
	[InlineData("""{"OnlyWhitelisted": false}""", false)]
	[InlineData("""{}""", false)]
	public void Режим_белого_списка(string config, bool expected)
	{
		Assert.Equal(expected, PlayerListsView.WhitelistOnly(JsonNode.Parse(config)!.AsObject()));
	}

	[Fact]
	public void Без_настроек_режим_неизвестен_но_списки_есть()
	{
		var response = PlayerListsView.Compose(lists, config: null, hideUid: false);

		Assert.True(response.Stale);
		Assert.False(response.WhitelistMode);
		Assert.Single(response.Banned);
	}
}
