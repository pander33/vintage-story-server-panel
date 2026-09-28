using Panel.SharedKernel;

namespace Panel.Fleet.Domain;

/// <summary>
/// Состояние реестра на момент чтения: прочитан или недоступен.
/// </summary>
/// <remarks>
/// «Реестр недоступен» и «реестр пуст» различаются: для пустого реестра мастер установки
/// предлагает создать первый сервер, а на машине, где серверы есть, это было бы ошибкой.
/// </remarks>
public abstract record RegistrySnapshot
{
	private RegistrySnapshot()
	{
	}

	/// <summary>Реестр прочитан. Пустой список допустим.</summary>
	public sealed record Available(
		IReadOnlyList<RegisteredServer> Servers,
		InstanceId? Default,
		RegistryStamp Stamp) : RegistrySnapshot
	{
		public RegisteredServer? Find(InstanceId id) =>
			Servers.FirstOrDefault(server => server.Id == id);

		/// <summary>
		/// Сервер по умолчанию: объявленный, если он существует, иначе первый. Объявленный, но
		/// отсутствующий сервер умолчанием не становится.
		/// </summary>
		public RegisteredServer? EffectiveDefault =>
			(Default is { } id ? Find(id) : null) ?? Servers.FirstOrDefault();
	}

	/// <summary>Реестр прочитать не удалось. Это не то же самое, что пустой реестр.</summary>
	public sealed record Unavailable(Phrase Detail) : RegistrySnapshot;
}

/// <summary>
/// Отметка версии файла реестра для сравнения перед записью. Запись от устаревшей отметки
/// отвергается, иначе две панели перезаписывали бы правки друг друга.
/// </summary>
public readonly record struct RegistryStamp(long Length, DateTimeOffset ModifiedAt)
{
	public static RegistryStamp Absent { get; } = new(-1, DateTimeOffset.MinValue);
}
