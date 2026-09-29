using Panel.Cli;
using Panel.Fleet.Domain;
using Panel.Fleet.Infrastructure;
using Panel.SharedKernel;

namespace Panel.Cli.Tests;

/// <summary>Какие серверы <c>vs-panel uninstall --purge</c> может стереть вместе с панелью.</summary>
public sealed class ServerPurgeTests
{
	private static PanelConfiguration Configuration(string json = """
		{"version": 2, "instances_file": "/etc/vs-panel/instances.json", "instances_root": "/srv/games", "former_instances_roots": ["/srv/vs-panel"]}
		""") => PanelConfiguration.Parse(json).Value;

	private static RegisteredServer Created(string id, string root = "/srv/games") => new()
	{
		Id = InstanceId.Parse(id),
		Name = id,
		Unit = $"vs-{id}.service",
		DataDirectory = $"{root}/{id}/data",
		ProgramDirectory = $"{root}/{id}/server",
	};

	[Fact]
	public void Сервер_панели_стирается_целиком()
	{
		var (created, foreign) = ServerPurge.Plan(Configuration(), [Created("main")]);

		var server = Assert.Single(created);
		Assert.Equal("/srv/games/main", server.Layout.Root);
		Assert.Equal("vs-main.service", server.Layout.Unit);
		Assert.Empty(foreign);
	}

	[Fact]
	public void Сервер_из_прежнего_каталога_тоже_панели()
	{
		var (created, _) = ServerPurge.Plan(Configuration(), [Created("old", "/srv/vs-panel")]);

		Assert.Equal("/srv/vs-panel/old", Assert.Single(created).Layout.Root);
	}

	/// <summary>Сервер, поставленный вручную до панели: его каталоги панели не принадлежат.</summary>
	[Fact]
	public void Чужой_сервер_не_трогается()
	{
		var legacy = new RegisteredServer
		{
			Id = InstanceId.Parse("main"),
			Name = "main",
			Unit = "vintagestory.service",
			DataDirectory = "/srv/vintagestory/data",
			ProgramDirectory = "/srv/vintagestory/server",
		};

		var (created, foreign) = ServerPurge.Plan(Configuration(), [legacy, Created("second")]);

		Assert.Equal("second", Assert.Single(created).Server.Id.Value);
		Assert.Same(legacy, Assert.Single(foreign));
	}

	[Fact]
	public void Реестр_версии_1_не_стирается()
	{
		var (created, foreign) = ServerPurge.Plan(Configuration("""{"instances_root": "/srv/games"}"""), [Created("main")]);

		Assert.Empty(created);
		Assert.Single(foreign);
	}
}
