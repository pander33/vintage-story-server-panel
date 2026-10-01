using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Panel.Access.Authorization;
using Panel.Access.Infrastructure;
using Panel.Access.Ports;
using Panel.Access.Sessions;
using Panel.Agent.Client;
using Panel.Catalogues.Domain;
using Panel.Catalogues.Infrastructure;
using Panel.Configuration.Infrastructure;
using Panel.Configuration.Ports;
using Panel.Content.Ports;
using Panel.Fleet.Infrastructure;
using Panel.Fleet.Ports;
using Panel.Maintenance.Infrastructure;
using Panel.Maintenance.Ports;
using Panel.Observability.Infrastructure;
using Panel.Observability.Ports;
using Panel.Provisioning.Ports;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;

namespace Panel.Web;

/// <summary>
/// Граф служб панели, общий для веб-процесса и <c>vs-panel-web manage</c>.
/// </summary>
/// <remarks>
/// Установщик создаёт первый сервер тем же <see cref="ProvisionService"/>, что и мастер: те же
/// вызовы демона, та же запись в истории, тот же откат. Поэтому граф один, а режим управления
/// просто не запускает хост и фоновые службы.
/// </remarks>
public static class PanelServices
{
	public static IServiceCollection AddPanel(this IServiceCollection services, PanelOptions options, SessionSecret secret)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(secret);

		services.AddSingleton(options);
		services.AddSingleton(TimeProvider.System);

		services.AddSingleton(secret);
		services.AddSingleton<SessionReader>();
		services.AddSingleton<SessionWriter>();
		services.AddSingleton<SessionGuard>();
		services.AddSingleton<CommandAuthorizer>();
		services.AddSingleton<LoginThrottle>();
		services.AddSingleton<IAccountStore>(_ =>
			new FileAccountStore(options.AccountsPath, options.LegacyPasswordPath));

		services.AddSingleton(_ => new JsonLinesFile(
			options.AuditPath, keep: 10_000, retentionDays: options.AuditRetentionDays));
		services.AddSingleton<AuditLog>();
		services.AddSingleton(_ => new NotificationSettingsFile(options.NotificationsPath));
		services.AddSingleton(_ => new NotificationSender(new HttpClient()));
		services.AddSingleton<EventBus>();
		services.AddHostedService<NotificationDispatcher>();

		services.AddSingleton<IRegistryStore>(_ => new FileRegistryStore(options.PanelConfigFile));

		var agent = new AgentClient(options.AgentSocket);
		services.AddSingleton(agent);
		services.AddSingleton<IServerRuntime>(_ => new AgentServerRuntime(agent));
		services.AddSingleton<ITelemetryReader>(_ => new AgentTelemetryReader(agent));
		services.AddSingleton<IModStore>(_ => new AgentModStore(agent));
		services.AddSingleton<IWorldStore>(_ => new AgentWorldStore(agent));
		services.AddSingleton<IBackupStore>(_ => new AgentBackupStore(agent));
		services.AddSingleton<IOperationProgress>(_ => new AgentOperationProgress(agent));
		services.AddSingleton<IHistoryStore>(_ => new AgentHistoryStore(agent));
		services.AddSingleton<IRegistryAdministration>(_ => new AgentRegistryAdministration(agent));
		services.AddSingleton<StagingWriter>();

		// Внешние каталоги: ModDB и официальный манифест версий. Один HttpClient на процесс, таймауты
		// задаются для каждого запроса внутри клиентов. Соединение перебирает адреса узла: провайдер
		// может не пропускать тот узел CDN, что выдал DNS.
		var connector = new FallbackConnector(new Dictionary<string, IReadOnlyList<System.Net.IPAddress>>
		{
			[ModDbClient.DownloadHost] = options.ModDbCdnAddresses,
		});
		var external = new HttpClient(connector.Handler(redirects: true));
		services.AddSingleton(new ModDbClient(external));
		services.AddSingleton(p => new ModCatalogueCache(p.GetRequiredService<ModDbClient>(), TimeProvider.System));

		// Картинки — через свой клиент без переадресаций: проверка хоста иначе не имела бы смысла.
		services.AddSingleton(_ => new ModImageProxy(new HttpClient(connector.Handler(redirects: false)), TimeProvider.System));
		services.AddSingleton(p => new VersionCatalogue(
			options.CatalogueCachePath,
			VersionCatalogue.FromNetwork(external),
			p.GetRequiredService<TimeProvider>(),
			// Пакет для платформы панели и демона: они всегда на одной машине.
			OperatingSystem.IsWindows() ? ServerPlatform.Windows : ServerPlatform.Linux));
		services.AddSingleton<GameVersions>();
		services.AddSingleton<IServerConfigStore>(_ => new AgentServerConfigStore(agent));
		services.AddSingleton<IWorldSchemaStore>(_ => new AgentWorldSchemaStore(agent));
		services.AddSingleton<ILiveWorldConfig>(_ => new AgentLiveWorldConfig(agent));
		services.AddSingleton<IPlayerCommands>(_ => new AgentPlayerCommands(agent));
		services.AddSingleton<IPlayerListStore>(_ => new AgentPlayerListStore(agent));
		services.AddSingleton<IGameEventFeed>(_ => new AgentGameEventFeed(agent));
		services.AddSingleton<ITelemetrySource>(_ => new AgentTelemetrySource(agent));
		services.AddSingleton<IMetricsSource>(_ => new AgentMetricsSource(agent));
		services.AddSingleton<ServerMonitors>();
		services.AddSingleton<HostProbe>();
		services.AddHostedService<MonitoringService>();
		services.AddSingleton<IApplyMarkerStore>(_ => new FileApplyMarkerStore(options.StateDirectory));

		services.AddSingleton<ReadinessPolicy>();
		services.AddSingleton<ReadinessLog>();
		services.AddSingleton<GetServerStatusHandler>();
		services.AddSingleton<ServerControlHandler>();
		services.AddSingleton<ServerConfigurationHandler>();

		// Аренда обслуживания на диске, одна на панель; она сохраняется после аварии процесса.
		services.AddSingleton(new FileLeaseStore(options.MaintenanceLeasePath));
		services.AddSingleton<ProcessProbe>();
		services.AddSingleton(provider => new MaintenanceCoordinator(
			provider.GetRequiredService<FileLeaseStore>(),
			provider.GetRequiredService<ProcessProbe>(),
			provider.GetRequiredService<TimeProvider>()));
		services.AddSingleton<IGamePackages>(_ => new AgentGamePackages(agent));
		services.AddSingleton<IProvisioningHost>(_ => new AgentProvisioningHost(agent));
		services.AddSingleton(new PackageDownloader(external));

		// Один экземпляр на панель: он и отвечает маршрутам, и выполняет фоновый такт.
		services.AddSingleton<Announcements>();
		services.AddSingleton<AutomationService>();
		services.AddSingleton<IReadinessWaiter>(provider => provider.GetRequiredService<AutomationService>());
		services.AddSingleton<UpdateService>();
		services.AddSingleton<TransferPlans>();
		services.AddSingleton<ProvisionService>();
		services.AddSingleton<SetupService>();
		services.AddSingleton<PanelInstance>();
		services.AddSingleton<InstanceRemoval>();
		services.AddSingleton<DiagnosticsHandler>();

		// Службы хоста стартуют в порядке регистрации: прошлый запуск разбирается раньше, чем такт
		// автоматизации возьмёт первую аренду.
		services.AddHostedService<OperationRecoveryService>();
		services.AddHostedService(provider => provider.GetRequiredService<SetupService>());
		services.AddHostedService(provider => provider.GetRequiredService<AutomationService>());
		services.AddHostedService(provider => provider.GetRequiredService<UpdateService>());
		services.AddHostedService(provider => provider.GetRequiredService<ProvisionService>());

		// Штатная остановка ждёт идущее обновление: наполовину заменённая программа сервера хуже
		// ожидания. Значение чуть меньше TimeoutStopSec=1900s юнита, чтобы выйти раньше SIGKILL.
		services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(1880));
		return services;
	}
}
