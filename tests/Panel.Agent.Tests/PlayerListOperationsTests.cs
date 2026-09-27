using System.Text.Json;
using Panel.Agent.Content;
using Panel.Agent.Transport;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Tests;

/// <summary>Операция <c>players-lists</c>: файлы игры читаются безопасно, отсутствие файла — не отказ.</summary>
public sealed class PlayerListOperationsTests : IDisposable
{
	private readonly string root = Path.Combine(Path.GetTempPath(), "player-lists", Guid.NewGuid().ToString("n"));

	public PlayerListOperationsTests() => Directory.CreateDirectory(Path.Combine(root, "data", "Playerdata"));

	public void Dispose()
	{
		try
		{
			Directory.Delete(root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	[Fact]
	public async Task Без_файлов_оба_списка_пусты()
	{
		var reply = await ExecuteAsync();

		Assert.True(reply.Ok);
		var payload = reply.Result.Deserialize<PlayerListsPayload>(PanelJson.Options)!;
		Assert.Empty(payload.Banned);
		Assert.Empty(payload.Whitelisted);
	}

	[Fact]
	public async Task Оба_файла_приходят_в_ответе()
	{
		Write("playersbanned.json", """[{"PlayerName": "Griefer", "PlayerUID": "abc=", "UntilDate": 1893456000}]""");
		Write("playerswhitelisted.json", """[{"PlayerName": "Friend"}]""");

		var reply = await ExecuteAsync();

		Assert.True(reply.Ok);
		var payload = reply.Result.Deserialize<PlayerListsPayload>(PanelJson.Options)!;
		var banned = Assert.Single(payload.Banned);
		Assert.Equal("Griefer", banned.Player);
		Assert.Equal("abc=", banned.Uid);
		Assert.Equal(1893456000, banned.Until);
		Assert.False(banned.Forever);
		var friend = Assert.Single(payload.Whitelisted);
		Assert.Equal("Friend", friend.Player);
		Assert.True(friend.Forever);
		Assert.Null(friend.Until);
	}

	[Fact]
	public async Task Битый_файл_это_отказ_invalid()
	{
		Write("playersbanned.json", "[{");

		var reply = await ExecuteAsync();

		Assert.False(reply.Ok);
		Assert.Equal("invalid", reply.Code);
	}

	[Fact]
	public async Task Слишком_большой_файл_не_читается()
	{
		Write("playerswhitelisted.json", new string(' ', 1024 * 1024 + 1));

		var reply = await ExecuteAsync();

		Assert.False(reply.Ok);
		Assert.Equal("invalid", reply.Code);
	}

	[Fact]
	public async Task Без_каталога_данных_отказ()
	{
		var reply = await new PlayerListOperations(new PlayerListsBackend())
			.ExecuteAsync(Request(), Server() with { DataDirectory = string.Empty }, CancellationToken.None);

		Assert.False(reply.Ok);
		Assert.Equal("unavailable", reply.Code);
	}

	private Task<AgentReply> ExecuteAsync() =>
		new PlayerListOperations(new PlayerListsBackend()).ExecuteAsync(Request(), Server(), CancellationToken.None);

	private static AgentRequest Request() =>
		new() { Op = AgentProtocol.OpPlayerLists, Instance = "example", Args = [] };

	private void Write(string name, string content) =>
		File.WriteAllText(Path.Combine(root, "data", "Playerdata", name), content);

	private RegisteredServer Server() => new()
	{
		Id = InstanceId.Parse("example"),
		Name = "Example",
		Unit = "vs-example",
		GameVersion = "1.22.7",
		DataDirectory = Path.Combine(root, "data"),
		WorldDirectory = Path.Combine(root, "data", "Saves"),
		ModsDirectory = Path.Combine(root, "data", "Mods"),
		ConfigFile = Path.Combine(root, "data", "serverconfig.json"),
	};
}
