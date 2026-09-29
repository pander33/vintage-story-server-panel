using Panel.Fleet.Domain;
using Panel.Fleet.Infrastructure;
using Panel.Provisioning.Domain;

namespace Panel.Cli;

/// <summary>Сервер из реестра, который удаление панели может стереть вместе с ней.</summary>
internal sealed record PurgedServer(RegisteredServer Server, InstanceLayout Layout);

/// <summary>
/// Какие серверы реестра стираются при <c>vs-panel uninstall --purge</c>, если оператор согласился.
/// </summary>
/// <remarks>
/// Правило то же, что у демона при удалении сервера из панели (<c>ProvisionBackend.DeleteInstanceAsync</c>):
/// стирается только сервер, пути которого выведены из текущего или прежнего каталога серверов.
/// Сервер, заведённый вручную до панели, указывает на каталоги, которыми панель не владеет, — он
/// остаётся в реестре и на диске.
/// </remarks>
internal static class ServerPurge
{
	public static (IReadOnlyList<PurgedServer> Created, IReadOnlyList<RegisteredServer> Foreign) Plan(
		PanelConfiguration configuration,
		IReadOnlyList<RegisteredServer> servers)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		ArgumentNullException.ThrowIfNull(servers);

		var created = new List<PurgedServer>();
		var foreign = new List<RegisteredServer>();
		foreach (var server in servers)
		{
			if (!configuration.IsReadOnlyRegistry && CreatedLayout(configuration, server) is { } layout)
			{
				created.Add(new PurgedServer(server, layout));
			}
			else
			{
				foreign.Add(server);
			}
		}

		return (created, foreign);
	}

	private static InstanceLayout? CreatedLayout(PanelConfiguration configuration, RegisteredServer server)
	{
		foreach (var root in configuration.FormerInstancesRoots.Append(configuration.InstancesRoot))
		{
			var layout = InstanceLayout.Create(root, configuration.RunDirectory, server.Id, configuration.Layout);
			if (layout.IsSuccess
				&& server.Unit == layout.Value.Unit
				&& server.DataDirectory == layout.Value.DataDirectory
				&& server.ProgramDirectory == layout.Value.ProgramDirectory)
			{
				return layout.Value;
			}
		}

		return null;
	}
}
