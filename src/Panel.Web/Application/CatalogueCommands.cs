using Panel.Access.Authorization;
using Panel.Access.Domain;
using Panel.Catalogues.Domain;
using Panel.Contracts.Content;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Каталог версий игры доступен администратору. Мастер установки и создание сервера читают его
/// со своими правами.
/// </summary>
[Requires(Permission.Administer)]
public sealed record ReadCatalogue : ICommand;

/// <summary>
/// Версия игры, под которую подбираются моды: <c>game_version</c> сервера из реестра, а если
/// её там нет, <c>PANEL_GAME_VERSION</c>.
/// </summary>
/// <remarks>
/// Реестр перечитывается при каждом запросе: после обновления игры версия в нём меняется.
/// Если реестр недоступен, берётся умолчание панели, и поиск модов продолжает работать.
/// </remarks>
public sealed class GameVersions
{
	private readonly IRegistryStore registry;
	private readonly PanelOptions options;

	public GameVersions(IRegistryStore registry, PanelOptions options)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(options);
		this.registry = registry;
		this.options = options;
	}

	public async Task<GameVersion> OfAsync(InstanceId instance, CancellationToken ct)
	{
		var snapshot = await registry.LoadAsync(ct).ConfigureAwait(false);
		var recorded = (snapshot as RegistrySnapshot.Available)?.Find(instance)?.GameVersion;
		return GameVersion.Create(recorded is { Length: > 0 } version ? version : options.GameVersion);
	}
}

/// <summary>
/// Проверка зависимостей в ответе <c>/api/mods</c>. Моды перечисляет демон, а зависимости
/// оценивает панель, потому что версию игры знает она.
/// </summary>
public static class ModDependencyReport
{
	public static ModsPayload Annotate(ModsPayload payload, GameVersion game)
	{
		ArgumentNullException.ThrowIfNull(payload);

		var subjects = payload.Mods
			.Select(mod => new DependencySubject(mod.ModId, mod.Version ?? string.Empty, mod.Enabled, mod.Dependencies))
			.ToList();
		var issues = DependencyPolicy.Evaluate(subjects, game);

		return payload with
		{
			Mods =
			[
				.. payload.Mods.Select((mod, index) => mod with
				{
					DependencyIssues = [.. issues[index].Select(ToPayload)],

                    // missing_dependencies пересчитывается по итогам той же проверки: отсутствующие в порядке
                    // кодов символов.
                    MissingDependencies = DependencyPolicy.Missing(issues[index]),
				}),
			],
		};
	}

	private static DependencyIssuePayload ToPayload(DependencyIssue issue) => new()
	{
		ModId = issue.ModId,
		Status = issue.StatusWire,
		Required = issue.Required,
		Installed = issue.Installed,
		Reason = issue.ReasonWire,
	};
}
