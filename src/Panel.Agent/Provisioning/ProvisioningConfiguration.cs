using Panel.Fleet.Domain;
using Panel.Fleet.Infrastructure;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Provisioning;

/// <summary>Настройки панели и вывод допустимых путей экземпляров.</summary>
internal sealed class ProvisioningConfiguration(string path, IProvisioningPlatform platform)
{
	internal string Path { get; } = path;

	/// <summary>Выбор каталога серверов по правилам <see cref="ServersRoot"/> и каталогам этой панели.</summary>
	internal Result<ServersRoot> Choose(PanelConfiguration configuration, string raw) =>
		ServersRoot.Create(
			raw,
			configuration.Layout,
			exclusive: [configuration.RunDirectory, configuration.StagingDirectory, configuration.TelemetryDirectory],
			containers: [Parent(configuration.InstancesFile), Parent(configuration.PanelEnvFile), Parent(Path)]);

	private static string Parent(string path) =>
		path.Length == 0 ? string.Empty : System.IO.Path.GetDirectoryName(path) ?? string.Empty;

	/// <summary>
	/// Раскладка сервера под каталогом серверов, в котором он создан: текущим или прежним.
	/// Если такого нет, берётся текущий, и сверка путей откажет, как серверу не от панели.
	/// </summary>
	internal Result<InstanceLayout> CreatedLayout(PanelConfiguration configuration, RegisteredServer server)
	{
		foreach (var former in configuration.FormerInstancesRoots)
		{
			var layout = Layout(configuration with { InstancesRoot = former }, server.Id);
			if (layout.IsSuccess && layout.Value.ProgramDirectory == server.ProgramDirectory)
			{
				return layout;
			}
		}

		return Layout(configuration, server.Id);
	}

	/// <summary>
	/// Раскладка сервера по настройкам панели. Раскладка из настроек должна совпадать с
	/// платформой демона: сервер с путями Windows, созданный на Linux, не запустится.
	/// </summary>
	internal Result<InstanceLayout> Layout(PanelConfiguration configuration, InstanceId id) =>
		configuration.Layout == platform.Layout
			? InstanceLayout.Create(configuration.InstancesRoot, configuration.RunDirectory, id, configuration.Layout)
			: Failure.Unavailable("Раскладка в настройках панели не совпадает с платформой демона");

	internal async Task<Result<PanelConfiguration>> ReadAsync(CancellationToken ct)
	{
		try
		{
			return PanelConfiguration.Parse(await File.ReadAllTextAsync(Path, ct).ConfigureAwait(false));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не читается {Path}: {exception.Message}");
		}
	}
}
