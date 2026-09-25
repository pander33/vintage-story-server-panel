using Panel.SharedKernel;

namespace Panel.Fleet.Domain;

/// <summary>
/// Отображаемое имя сервера: без управляющих символов, не пустое, не длиннее 64.
/// </summary>
public readonly record struct InstanceName
{
	private const int maxLength = 64;

	private InstanceName(string value) => Value = value;

	public string Value { get; }

	public static Result<InstanceName> Create(string? raw)
	{
		var value = (raw ?? string.Empty).Trim();

		return value.Length == 0 || value.EnumerateRunes().Count() > maxLength || value.Any(symbol => symbol < ' ')
			? Failure.Invalid("Недопустимое имя сервера")
			: new InstanceName(value);
	}

	public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// Правила изменения реестра, не зависящие от файла: какой сервер станет умолчанием и какой
/// можно убрать.
/// </summary>
public static class RegistryRules
{
	/// <summary>
	/// Сервер для запроса: названный, если он зарегистрирован, иначе умолчание. Незнакомое имя
	/// не ошибка: реестр мог измениться, пока вкладка была открыта.
	/// </summary>
	public static InstanceId Resolve(RegistrySnapshot snapshot, InstanceId? requested, InstanceId panelInstance)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		if (snapshot is not RegistrySnapshot.Available available)
		{
			// Реестр не прочитан, и проверить регистрацию нельзя. Названный сервер остаётся, иначе
			// кратковременный сбой чтения перебрасывал бы пользователя на другой сервер.
			return requested ?? panelInstance;
		}

		if (requested is { } named && available.Find(named) is not null)
		{
			return named;
		}

		return EffectiveDefault(available, panelInstance);
	}

	/// <summary>
	/// Умолчание реестра, если такой сервер зарегистрирован, иначе <c>PANEL_INSTANCE</c>: так
	/// выглядит машина до мастера установки.
	/// </summary>
	public static InstanceId EffectiveDefault(RegistrySnapshot.Available registry, InstanceId panelInstance)
	{
		ArgumentNullException.ThrowIfNull(registry);

		return registry.Default is { } declared && registry.Find(declared) is not null
			? declared
			: panelInstance;
	}

	/// <summary>
	/// Можно ли убрать сервер из реестра. Единственный нельзя: панель без серверов обращается к
	/// <c>PANEL_INSTANCE</c>, которого тогда нигде нет.
	/// </summary>
	public static Result<Done> CanRemove(IReadOnlyCollection<InstanceId> registered, InstanceId target)
	{
		ArgumentNullException.ThrowIfNull(registered);

		if (!registered.Contains(target))
		{
			return Failure.NotFound($"Неизвестный сервер {target}");
		}

		return registered.Count == 1
			? Failure.Conflict("Нельзя убрать единственный сервер")
			: Done.Ok;
	}

	/// <summary>
	/// Умолчание после удаления сервера: прежнее, если удалён другой сервер, иначе первый из
	/// оставшихся.
	/// </summary>
	public static InstanceId DefaultAfterRemoving(
		IReadOnlyCollection<InstanceId> registered,
		InstanceId? currentDefault,
		InstanceId removed)
	{
		ArgumentNullException.ThrowIfNull(registered);

		if (currentDefault is { } kept && kept != removed)
		{
			return kept;
		}

		return registered
			.Where(id => id != removed)
			.OrderBy(id => id.Value, StringComparer.Ordinal)
			.First();
	}
}
