using System.Text.Json;
using Panel.Contracts;
using Panel.Contracts.Content;

namespace Panel.Contracts.Tests;

/// <summary>
/// Форма ответов о содержимом, заморожена (ADR-16): фронтенд читает эти имена.
/// </summary>
/// <remarks>
/// Сравниваются имена полей, а не значения. Переименование поля ломает интерфейс, а компилятор
/// этого не заметит: он видит свойство C#, а не строку в JavaScript.
/// </remarks>
public sealed class ContentShapeTests
{
	[Fact]
	public void Перечень_модов_несёт_обещанные_поля()
	{
		var json = Serialize(new ModsPayload
		{
			Mods =
			[
				new ModEntryPayload
				{
					Name = "survival_1.0.zip",
					Enabled = true,
					Size = 1024,
					Modified = 1_700_000_000,
					ModId = "survival",
					Version = "1.0.0",
					DisplayName = "Выживание",
					Side = "server",
				},
			],
			Enabled = 1,
		});

		foreach (var field in new[]
		{
			"mods", "enabled", "duplicate_ids", "rollbacks", "profiles",
			"name", "size", "modified", "modid", "version", "display_name", "side",
			"dependencies", "missing_dependencies", "error",
		})
		{
			Assert.Contains($"\"{field}\"", json, StringComparison.Ordinal);
		}
	}

	/// <summary>
	/// Кириллица остаётся кириллицей: FastAPI отдаёт UTF-8, а названия модов сравниваются с
	/// золотыми фикстурами.
	/// </summary>
	[Fact]
	public void Кириллица_не_экранируется() =>
		Assert.Contains("Выживание", Serialize(new ModEntryPayload
		{
			Name = "survival_1.0.zip",
			Enabled = true,
			Size = 1,
			Modified = 1,
			DisplayName = "Выживание",
		}), StringComparison.Ordinal);

	[Fact]
	public void Перечень_миров_несёт_обещанные_поля()
	{
		var json = Serialize(new WorldsPayload
		{
			Worlds =
			[
				new WorldEntryPayload
				{
					Name = "default.vcdbs",
					Size = 1024,
					Modified = 1_700_000_000,
					Active = true,
				},
			],
			Active = "default.vcdbs",
			ServerActive = false,
		});

		foreach (var field in new[] { "worlds", "active", "server_active", "name", "size", "modified" })
		{
			Assert.Contains($"\"{field}\"", json, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void Убранный_мир_несёт_и_хранимое_и_исходное_имя()
	{
		var json = Serialize(new StoredWorldPayload
		{
			StoredName = "20260921-100000-world.vcdbs",
			OriginalName = "world.vcdbs",
			Mode = "archive",
			Size = 1,
			Modified = 1,
		});

		Assert.Contains("\"stored_name\"", json, StringComparison.Ordinal);
		Assert.Contains("\"original_name\"", json, StringComparison.Ordinal);
		Assert.Contains("\"mode\"", json, StringComparison.Ordinal);
	}

	[Fact]
	public void Удержание_несёт_все_поля_предпросмотра()
	{
		var json = Serialize(new RetentionPayload { Mode = "preview" });

		foreach (var field in new[]
		{
			"mode", "candidates", "removed", "bytes", "free_before", "free_after", "target_free", "target_met",
		})
		{
			Assert.Contains($"\"{field}\"", json, StringComparison.Ordinal);
		}
	}

	/// <summary>
	/// Незаполненное необязательное поле остаётся в ответе как <c>null</c>: фронтенд читает
	/// <c>modid</c>, не проверяя наличие ключа.
	/// </summary>
	[Fact]
	public void Необязательное_поле_остаётся_в_ответе_как_null() =>
		Assert.Contains("\"modid\":null", Serialize(new ModChangePayload { Name = "a.zip" }), StringComparison.Ordinal);

	/// <summary>
	/// <c>dependency_issues</c> заполняет панель по версии игры: у отсутствующей зависимости
	/// <c>installed</c> равен null, <c>reason</c> есть только у непроверенной,
	/// <c>missing_dependencies</c> пересчитывается той же проверкой. В ответе демона этого поля нет.
	/// </summary>
	[Fact]
	public void Зависимости_модов_оцениваются_по_версии_игры()
	{
		static ModEntryPayload Mod(string id, string version, Dictionary<string, string>? dependencies = null) => new()
		{
			Name = id + ".zip",
			Enabled = true,
			Size = 1,
			Modified = 1,
			ModId = id,
			Version = version,
			Dependencies = dependencies ?? [],
		};

		var payload = new ModsPayload
		{
			Mods =
			[
				Mod("library", "1.2.0"),
				Mod("consumer", "1.0.0", new() { ["library"] = "1.3.0", ["Zlib"] = "1.0", ["alib"] = "", ["game"] = "1.22.0" }),
			],
		};
		Assert.DoesNotContain("dependency_issues", Serialize(payload), StringComparison.Ordinal);

		var annotated = Panel.Web.Application.ModDependencyReport.Annotate(
			payload, Panel.Catalogues.Domain.GameVersion.Create("1.22.7"));
		var consumer = Serialize(annotated.Mods[1]);

		Assert.Contains(
			"\"dependency_issues\":[{\"modid\":\"library\",\"status\":\"outdated\",\"required\":\"1.3.0\",\"installed\":\"1.2.0\"},"
			+ "{\"modid\":\"Zlib\",\"status\":\"missing\",\"required\":\"1.0\",\"installed\":null},"
			+ "{\"modid\":\"alib\",\"status\":\"missing\",\"required\":\"\",\"installed\":null}]",
			consumer,
			StringComparison.Ordinal);

		// Порядок по кодам символов: заглавные раньше строчных.
		Assert.Equal(["Zlib", "alib"], annotated.Mods[1].MissingDependencies);
		Assert.Contains("\"dependency_issues\":[]", Serialize(annotated.Mods[0]), StringComparison.Ordinal);
	}

	private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, PanelJson.Options);
}
