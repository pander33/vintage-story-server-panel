using System.Text.Json;
using Panel.Contracts.Configuration;
using Panel.Contracts.Http;
using Panel.SharedKernel;
using Panel.Web.Application;

namespace Panel.Contracts.Tests;

/// <summary>
/// Форма ответов о настройках, заморожена (ADR-16): фронтенд читает эти имена.
/// </summary>
public sealed class ConfigurationShapeTests
{
	[Fact]
	public void Описание_настроек_несёт_обещанные_поля()
	{
		var json = Serialize(new ConfigDescriptionResponse
		{
			Schema = new ConfigSchemaResponse
			{
				Categories = [new ConfigCategoryResponse { Id = "general", Label = Phrase.From("Основные") }],
				Fields = [new ConfigFieldResponse { Key = "Port", Options = [new ConfigOptionResponse()] }],
			},
			Backups = [new ConfigBackupPayload { Name = "a" }],
		});

		foreach (var field in new[]
		{
			"config", "revision", "validation_warning", "schema", "apply_state", "backups",
			"categories", "fields", "id", "label", "key", "category", "type", "description",
			"min", "max", "options", "value", "restart_required", "nullable", "maximum_length",
			"state", "active", "changed_keys", "saved_at", "source", "name", "size", "modified",
		})
		{
			Assert.Contains($"\"{field}\"", json, StringComparison.Ordinal);
		}
	}

	/// <summary>Граница поля без предела остаётся в ответе как <c>null</c>.</summary>
	[Fact]
	public void Пустые_границы_поля_остаются_null()
	{
		var json = Serialize(new ConfigFieldResponse { Key = "AllowPvP", Type = "boolean" });

		Assert.Contains("\"min\":null", json, StringComparison.Ordinal);
		Assert.Contains("\"maximum_length\":null", json, StringComparison.Ordinal);
	}

	[Fact]
	public void Сохранение_и_откат_несут_обещанные_поля()
	{
		var saved = Serialize(new ConfigSaveResponse());
		var rolled = Serialize(new ConfigRollbackResponse());

		foreach (var field in new[] { "ok", "backup", "restart_required", "auto_restarted", "apply_state" })
		{
			Assert.Contains($"\"{field}\"", saved, StringComparison.Ordinal);
		}

		foreach (var field in new[] { "ok", "restored", "safety_backup", "restart_required", "auto_restarted", "apply_state" })
		{
			Assert.Contains($"\"{field}\"", rolled, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void Предпросмотр_несёт_значения_до_и_после()
	{
		var json = Serialize(new ConfigPreviewResponse
		{
			Changes = [new ConfigChangeResponse { Key = "Port", Label = Phrase.From("Порт") }],
		});

		Assert.Contains("\"before\":null", json, StringComparison.Ordinal);
		Assert.Contains("\"after\":null", json, StringComparison.Ordinal);
	}

	/// <summary>Ревизия шаблона — число (наносекунды), а не хеш, как у всего файла.</summary>
	[Fact]
	public void Ревизия_шаблона_мира_число() =>
		Assert.Contains(
			"\"revision\":1700000000000000000",
			Serialize(new WorldTemplateResponse { Revision = 1_700_000_000_000_000_000 }),
			StringComparison.Ordinal);

	[Fact]
	public void Живая_конфигурация_несёт_обещанные_поля()
	{
		var json = Serialize(new LiveWorldConfigResponse());

		foreach (var field in new[] { "available", "configuration", "detail" })
		{
			Assert.Contains($"\"{field}\"", json, StringComparison.Ordinal);
		}
	}

	private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, LocalizedJson.For(PanelLanguage.Russian));
}
