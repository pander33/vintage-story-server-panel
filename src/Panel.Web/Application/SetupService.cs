using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Panel.Access.Ports;
using Panel.Contracts;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Состояние мастера установки (<c>setup.json</c>) и токен захвата (<c>setup.token</c>).
/// </summary>
/// <remarks>
/// <para>
/// Каждое изменение идёт через <see cref="ChangeAsync{T}"/>: под одним семафором агрегат
/// принимает решение, файл записывается, и только потом клиент получает ответ. Ответ шага
/// записывается до действия по нему, поэтому после закрытия браузера, неудачного скачивания
/// или перезапуска панели мастер продолжается с того же места.
/// </para>
/// <para>
/// Если при старте у панели уже есть администратор, а её сервер есть в реестре, мастер не
/// открывается. При нечитаемом реестре мастер остаётся открытым: так безопаснее (§4.4
/// заметки).
/// </para>
/// </remarks>
public sealed partial class SetupService : IHostedService
{
	private readonly SemaphoreSlim gate = new(1, 1);
	private readonly PanelOptions options;
	private readonly IAccountStore accounts;
	private readonly IRegistryStore registry;
	private readonly TimeProvider clock;
	private readonly ILogger<SetupService> log;
	private SetupJourney? journey;

	public SetupService(
		PanelOptions options,
		IAccountStore accounts,
		IRegistryStore registry,
		TimeProvider clock,
		ILogger<SetupService> log)
	{
		this.options = options;
		this.accounts = accounts;
		this.registry = registry;
		this.clock = clock;
		this.log = log;
		ClaimThrottle = new LoginThrottle(clock);
	}

	/// <summary>
	/// Ограничение попыток захвата: те же 8 за 5 минут, что при входе, но свой счётчик, общий для
	/// всех адресов.
	/// </summary>
	public LoginThrottle ClaimThrottle { get; }

	public const string ClaimKey = "setup-claim";

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		var journey = await CurrentAsync(cancellationToken).ConfigureAwait(false);
		if (journey.IsCompleted)
		{
			return;
		}

		var directory = await accounts.LoadAsync(cancellationToken).ConfigureAwait(false);
		var hasAdministrator = directory.IsSuccess && !directory.Value.IsEmpty;
		var snapshot = await registry.LoadAsync(cancellationToken).ConfigureAwait(false);
		if (hasAdministrator && snapshot is RegistrySnapshot.Unavailable unavailable)
		{
			RegistryUnreadable(log, unavailable.Detail.ToString());
		}
		else if (InstanceId.Create(options.DefaultInstance) is { IsSuccess: true } panelInstance)
		{
			var adopted = await ChangeAsync(
					current => Result.Ok(current.Adopt(panelInstance.Value, hasAdministrator, Registered(snapshot), Now())),
					cancellationToken)
				.ConfigureAwait(false);
			if (adopted is { IsSuccess: true, Value: true })
			{
				return;
			}
		}

		NotConfigured(log);
	}

	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <summary>Текущее состояние мастера; возвращается копия.</summary>
	public async Task<SetupJourney> CurrentAsync(CancellationToken ct)
	{
		await gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			return SetupJourney.FromJson(Load().ToJson());
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>
	/// Изменить мастер одной операцией: решение агрегата и запись файла под семафором. При отказе
	/// агрегата файл не меняется.
	/// </summary>
	public async Task<Result<T>> ChangeAsync<T>(Func<SetupJourney, Result<T>> change, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(change);
		await gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			var journey = SetupJourney.FromJson(Load().ToJson());
			var outcome = change(journey);
			if (!outcome.IsSuccess)
			{
				return outcome;
			}

			var saved = await SaveAsync(journey, ct).ConfigureAwait(false);
			if (!saved.IsSuccess)
			{
				return saved.Failure!;
			}

			this.journey = journey;
			return outcome;
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>Есть ли хотя бы одна учётная запись; если справочник не читается, отказ, а не «нет».</summary>
	public async Task<Result<bool>> HasAdministratorAsync(CancellationToken ct)
	{
		var directory = await accounts.LoadAsync(ct).ConfigureAwait(false);
		return directory.IsSuccess ? !directory.Value.IsEmpty : directory.Failure!;
	}

	/// <summary>Токен из файла; <c>null</c>, если файла нет или он пуст.</summary>
	public ClaimToken? ReadToken()
	{
		try
		{
			return ClaimToken.Create(File.ReadAllText(options.SetupTokenPath));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>Токен использован, файл удаляется.</summary>
	public void ConsumeToken()
	{
		try
		{
			File.Delete(options.SetupTokenPath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			TokenNotDeleted(log, options.SetupTokenPath, exception);
		}
	}

	/// <summary>Зарегистрированные серверы; при нечитаемом реестре пустое множество, и мастер остаётся открытым.</summary>
	public static IReadOnlySet<InstanceId> Registered(RegistrySnapshot snapshot) =>
		snapshot is RegistrySnapshot.Available available
			? available.Servers.Select(server => server.Id).ToHashSet()
			: new HashSet<InstanceId>();

	private SetupJourney Load()
	{
		if (journey is not null)
		{
			return journey;
		}

		JsonNode? node = null;
		try
		{
			node = JsonNode.Parse(File.ReadAllText(options.SetupStatePath));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			// Как read_json(path, {}): если файла нет или он испорчен, мастер начинается сначала.
		}

		journey = SetupJourney.FromJson(node);
		return journey;
	}

	/// <summary>Атомарная запись: отступ в два пробела, перевод строки в конце, права 0600.</summary>
	private async Task<Result<Done>> SaveAsync(SetupJourney journey, CancellationToken ct)
	{
		var written = await AtomicFile
			.WriteAsync(options.SetupStatePath, journey.ToJson().ToJsonString(PanelJson.Indented) + "\n", ct)
			.ConfigureAwait(false);
		if (written.IsSuccess && !OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(options.SetupStatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}

		return written;
	}

	private long Now() => clock.GetUtcNow().ToUnixTimeSeconds();

	[LoggerMessage(Level = LogLevel.Warning, Message = "Панель не настроена: откройте /setup, чтобы создать первый сервер")]
	private static partial void NotConfigured(ILogger logger);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Реестр серверов не прочитан, мастер установки остаётся доступен: {Detail}")]
	private static partial void RegistryUnreadable(ILogger logger, string detail);

	[LoggerMessage(Level = LogLevel.Error, Message = "Токен захвата {Path} не удалён")]
	private static partial void TokenNotDeleted(ILogger logger, string path, Exception exception);
}
