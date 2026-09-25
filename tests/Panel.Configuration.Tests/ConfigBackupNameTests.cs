using Panel.Configuration.Domain;

namespace Panel.Configuration.Tests;

/// <summary>
/// Имя копии настроек становится частью пути, по которому демон читает и пишет от root.
/// </summary>
public sealed class ConfigBackupNameTests
{
	[Theory]
	[InlineData("serverconfig.json.panel-20260921-143000.bak", true)]
	[InlineData("serverconfig.json.panel-20260921-143000-2.bak", true)]
	[InlineData("serverconfig.json.bak", false)]
	[InlineData("serverconfig.json.panel-2026092-143000.bak", false)]
	[InlineData("../serverconfig.json.panel-20260921-143000.bak", false)]
	[InlineData("serverconfig.json.panel-20260921-143000.bak\n", true)]
	[InlineData("serverconfig.json.panel-20260921-143000.bak.tmp", false)]
	[InlineData("", false)]
	[InlineData(null, false)]
	public void Общая_форма(string? raw, bool accepted) =>
		Assert.Equal(accepted, ConfigBackupName.Create(raw).IsSuccess);

	/// <summary>Демон проверяет и то, с какого файла снята копия.</summary>
	[Fact]
	public void Копия_чужого_файла_не_подходит()
	{
		Assert.True(ConfigBackupName.CreateFor("serverconfig.json.panel-20260921-143000.bak", "serverconfig.json").IsSuccess);
		Assert.False(ConfigBackupName.CreateFor("other.json.panel-20260921-143000.bak", "serverconfig.json").IsSuccess);
		Assert.False(ConfigBackupName.CreateFor("serverconfigXjson.panel-20260921-143000.bak", "serverconfig.json").IsSuccess);
	}
}
