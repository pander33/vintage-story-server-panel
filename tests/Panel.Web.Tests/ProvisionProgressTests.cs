using System.Text.Json.Nodes;
using Panel.Web.Application;

namespace Panel.Web.Tests;

/// <summary>
/// Ход создания сервера в <c>manage provision</c>: установщик не молчит минутами.
/// </summary>
public sealed class ProvisionProgressTests
{
	private static readonly DateTimeOffset start = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

	private readonly StringWriter output = new();

	private long? staged;

	private ProvisionProgress Progress() => new(output, start, () => staged);

	private static JsonObject Operation(params JsonObject[] steps) =>
		new() { ["steps"] = new JsonArray([.. steps.Select(step => step.DeepClone())]) };

	private static JsonObject Step(string action, string status = "ok") =>
		new() { ["action"] = action, ["status"] = status };

	private string[] Lines() =>
		output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

	[Fact]
	public void Каждый_шаг_печатается_один_раз_по_мере_записи()
	{
		var progress = Progress();
		var catalogue = Step("catalogue");
		catalogue["version"] = "1.21.1";
		catalogue["size"] = 120L * 1024 * 1024;
		var download = Step("download");
		download["bytes"] = 120L * 1024 * 1024;

		progress.Report(Operation(catalogue), start.AddSeconds(2));
		progress.Report(Operation(catalogue), start.AddSeconds(4));
		progress.Report(Operation(catalogue, download, Step("create")), start.AddSeconds(6));

		Assert.Equal(
			["  Версия 1.21.1, пакет 120 МБ", "  Пакет скачан и сверен: 120 МБ", "  Распаковка и настройка сервера: готово"],
			Lines());
	}

	[Fact]
	public void Долгий_шаг_напоминает_о_себе_раз_в_15_секунд_со_скачанным_объёмом()
	{
		var progress = Progress();
		var catalogue = Step("catalogue");
		catalogue["version"] = "1.21.1";
		catalogue["size"] = 100L * 1024 * 1024;
		progress.Report(Operation(catalogue), start.AddSeconds(1));

		staged = 40L * 1024 * 1024;
		progress.Report(Operation(catalogue), start.AddSeconds(10));
		Assert.Single(Lines());

		progress.Report(Operation(catalogue), start.AddSeconds(16));
		progress.Report(Operation(catalogue), start.AddSeconds(20));
		staged = 75L * 1024 * 1024;
		progress.Report(Operation(catalogue), start.AddSeconds(95));

		Assert.Equal(
			["  Версия 1.21.1, пакет 100 МБ", "  … скачивание пакета игры: 40 из 100 МБ (прошло 0:16)", "  … скачивание пакета игры: 75 из 100 МБ (прошло 1:35)"],
			Lines());
	}

	[Fact]
	public void До_первой_записи_операции_идёт_выбор_версии()
	{
		Progress().Report(null, start.AddSeconds(15));

		Assert.Equal(["  … выбор версии игры (прошло 0:15)"], Lines());
	}

	[Fact]
	public void Отказ_шага_печатается_с_причиной()
	{
		var failed = Step("create", "failed");
		failed["detail"] = "Архив повреждён";
		var rollback = Step("rollback", "skipped");
		rollback["detail"] = "Сервер уже зарегистрирован; удалите его вручную, если он не нужен";

		Progress().Report(Operation(Step("catalogue"), Step("download"), failed, rollback), start.AddSeconds(3));

		Assert.Contains("  Ошибка на шаге «распаковка и настройка сервера»: Архив повреждён", Lines());
		Assert.Contains("  Сервер уже зарегистрирован; удалите его вручную, если он не нужен", Lines());
	}

	[Fact]
	public void Неподтверждённая_готовность_не_выдаётся_за_готовность()
	{
		var ready = Step("ready", "warning");
		ready["detail"] = "Ограниченная проверка: нет мода телеметрии";

		Progress().Report(Operation(ready), start.AddSeconds(3));

		Assert.Equal(["  Готовность сервера не подтверждена: Ограниченная проверка: нет мода телеметрии"], Lines());
	}
}
