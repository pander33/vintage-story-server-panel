using System.Globalization;
using System.Text.Json.Nodes;

namespace Panel.Web.Application;

/// <summary>
/// Ход создания сервера для <c>manage provision</c>: каждый шаг выводится строкой, как только
/// записан в операцию, а пока шаг идёт, раз в <see cref="Heartbeat"/> выводится «идёт …».
/// </summary>
/// <remarks>
/// Создание сервера занимает минуты: пакет игры в сотни мегабайт, распаковка, первый запуск.
/// Без этих строк установщик молчал бы до конца, и работу нельзя было бы отличить от
/// зависания. Источник тот же, что у мастера в браузере: запись операции.
/// </remarks>
public sealed class ProvisionProgress
{
	/// <summary>Как часто сообщать, что долгий шаг ещё идёт.</summary>
	public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

	private const double megabyte = 1024 * 1024;

	private readonly TextWriter output;
	private readonly DateTimeOffset started;
	private readonly Func<long?> downloaded;
	private int reported;
	private long packageSize;
	private DateTimeOffset lastLine;

	/// <param name="output">Куда писать.</param>
	/// <param name="started">Начало создания, от него считается прошедшее время.</param>
	/// <param name="downloaded">Сколько байт пакета уже на диске; <see langword="null"/>, если неизвестно.</param>
	public ProvisionProgress(TextWriter output, DateTimeOffset started, Func<long?> downloaded)
	{
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(downloaded);
		this.output = output;
		this.started = started;
		this.downloaded = downloaded;
		lastLine = started;
	}

	/// <summary>
	/// Вывести новое: шаги, записанные с прошлого раза, или, если их нет дольше
	/// <see cref="Heartbeat"/>, текущий шаг. <paramref name="operation"/> — запись операции
	/// (<c>current</c> снимка); <see langword="null"/>, если её ещё или уже нет.
	/// </summary>
	public void Report(JsonObject? operation, DateTimeOffset now)
	{
		var steps = (operation?["steps"] as JsonArray ?? []).OfType<JsonObject>().ToList();
		for (; reported < steps.Count; reported++)
		{
			output.WriteLine("  " + Describe(steps[reported]));
			lastLine = now;
		}

		if (now - lastLine >= Heartbeat)
		{
			output.WriteLine($"  … {Pending(steps.Count)} (прошло {Elapsed(now)})");
			lastLine = now;
		}

		output.Flush();
	}

	/// <summary>Завершённый шаг.</summary>
	private string Describe(JsonObject step)
	{
		var action = Text(step["action"]);
		var status = Text(step["status"]);
		var detail = Text(step["detail"]);
		if (status == "failed")
		{
			return $"Ошибка на шаге «{Title(action)}»{(detail.Length > 0 ? ": " + detail : string.Empty)}";
		}

		if (status == "skipped")
		{
			return detail.Length > 0 ? detail : Capitalized(Title(action)) + ": пропущено";
		}

		if (action == "ready")
		{
			return status == "ok"
				? "Сервер готов"
				: "Готовность сервера не подтверждена" + (detail.Length > 0 ? ": " + detail : string.Empty);
		}

		switch (action)
		{
			case "catalogue":
				packageSize = Number(step["size"]);
				return $"Версия {Text(step["version"])}" + (packageSize > 0 ? $", пакет {Megabytes(packageSize)} МБ" : string.Empty);
			case "download":
				return $"Пакет скачан и сверен: {Megabytes(Number(step["bytes"]))} МБ";
			default:
				return Capitalized(Title(action)) + ": готово";
		}
	}

	/// <summary>Шаг, который идёт после <paramref name="done"/> завершённых.</summary>
	private string Pending(int done) => done switch
	{
		0 => "выбор версии игры",
		1 => "скачивание пакета игры" + DownloadProgress(),
		2 => "распаковка и первичная настройка сервера",
		3 => "регистрация сервера у демона",
		4 => "запуск сервера",
		_ => "ожидание готовности сервера",
	};

	private string DownloadProgress()
	{
		if (downloaded() is not { } bytes)
		{
			return string.Empty;
		}

		return packageSize > 0
			? $": {Megabytes(bytes)} из {Megabytes(packageSize)} МБ"
			: $": {Megabytes(bytes)} МБ";
	}

	private static string Title(string action) => action switch
	{
		"catalogue" => "выбор версии",
		"download" => "скачивание пакета",
		"create" => "распаковка и настройка сервера",
		"service" => "регистрация сервера у демона",
		"start" => "запуск сервера",
		"rollback" => "откат",
		_ => action,
	};

	private static string Capitalized(string text) =>
		text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];

	private string Elapsed(DateTimeOffset now)
	{
		var elapsed = now - started;
		return elapsed.TotalHours >= 1
			? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
			: elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);
	}

	private static string Megabytes(long bytes) => (bytes / megabyte).ToString("0", CultureInfo.InvariantCulture);

	private static string Text(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

	private static long Number(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;
}
