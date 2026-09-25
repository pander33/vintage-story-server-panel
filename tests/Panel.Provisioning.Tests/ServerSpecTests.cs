using System.Text.Json.Nodes;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Provisioning.Tests;

/// <summary>Три проверки описания сервера: мастер, <c>/api/provision/create</c> и демон.</summary>
public sealed class ServerSpecTests
{
	private static readonly ServerAnswers spec = new()
	{
		Id = "creative",
		Name = "Творческий",
		Version = "1.22.7",
		Port = 42421,
		ServerName = "Творческий сервер",
		Description = string.Empty,
		MaxPlayers = 16,
		Whitelist = false,
		Pvp = true,
		Language = "ru",
		WorldName = "Новый мир",
		Seed = string.Empty,
		Playstyle = "creativebuilding",
	};

	[Fact]
	public void Пустые_имена_мастер_заполняет_сам()
	{
		var spec = ServerSpec.FromWizard(ServerSpecTests.spec with { Name = "", ServerName = "", WorldName = "  " }).Value;

		Assert.Equal("creative", spec.Name);
		Assert.Equal("creative", spec.ServerName);
		Assert.Equal("Новый мир", spec.WorldName);
	}

	public static TheoryData<ServerAnswers> Плохие => new()
	{
		spec with { Id = "Creative" },
		spec with { Id = "-creative" },
		spec with { Id = "" },
		spec with { Id = "-" },
		spec with { Port = 1023 },
		spec with { Port = 70000 },
		spec with { Playstyle = "godmode" },
		spec with { Language = "кириллица" },
		spec with { MaxPlayers = 0 },
		spec with { Version = "" },
		spec with { Description = new string('д', 257) },
	};

	[Theory]
	[MemberData(nameof(Плохие))]
	public void Плохая_настройка_до_демона_не_доходит(ServerAnswers answers)
	{
		var refused = ServerSpec.FromWizard(answers);

		Assert.Equal(FailureCode.Invalid, refused.Failure!.Code);
	}

	[Fact]
	public void Длина_считается_в_символах()
	{
		// 64 эмодзи — это 128 единиц UTF-16, но 64 символа.
		var name = string.Concat(Enumerable.Repeat("😀", 64));

		Assert.True(ServerSpec.FromWizard(spec with { Name = name }).IsSuccess);
		Assert.False(ServerSpec.FromWizard(spec with { Name = name + "a" }).IsSuccess);
	}

	[Fact]
	public void Запрос_создания_проверяет_только_своё_и_отвечает_конфликтом()
	{
		// validate_spec не проверяет языки и стили, это делает демон.
		Assert.True(ServerSpec.FromProvisionRequest(spec with { Language = "кириллица", Playstyle = "godmode" }).IsSuccess);

		var refused = ServerSpec.FromProvisionRequest(spec with { Port = 80 });
		Assert.Equal(FailureCode.Conflict, refused.Failure!.Code);
		Assert.Equal("Игровой порт должен быть от 1024 до 65535", refused.Failure.Message);
	}

	[Fact]
	public void Черновик_читается_обратно_тем_же_описанием()
	{
		var spec = ServerSpec.FromWizard(ServerSpecTests.spec).Value;

		var restored = ServerSpec.FromDraft(spec.ToJson()).Value;

		Assert.Equal(spec, restored);
		Assert.Equal("Сначала заполните настройки сервера", ServerSpec.FromDraft(new JsonObject()).Failure!.Message);
	}

	[Fact]
	public void Описание_для_демона_несёт_пакет_и_порядок_ключей()
	{
		var json = ServerSpec.FromWizard(spec).Value.ToJson(StagedFile.Create("game-ab.tar.gz").Value);

		Assert.Equal(
			["id", "name", "version", "port", "server_name", "description", "max_players", "whitelist", "pvp", "language", "world_name", "seed", "playstyle", "package"],
			json.Select(pair => pair.Key));
		Assert.Equal("game-ab.tar.gz", (string?) json["package"]);
	}

	[Fact]
	public void Демон_принимает_полное_описание()
	{
		var raw = ServerSpec.FromWizard(ServerSpecTests.spec).Value.ToJson(StagedFile.Create("game-ab.tar.gz").Value);
		raw["name"] = "  Творческий  ";

		var (spec, package) = InstanceSpecification.Parse(raw).Value;

		Assert.Equal("Творческий", spec.Name);
		Assert.Equal("game-ab.tar.gz", package);
	}

	[Fact]
	public void Демону_нельзя_ни_лишнего_ни_недостающего_ключа()
	{
		var raw = ServerSpec.FromWizard(spec).Value.ToJson(StagedFile.Create("game-ab.tar.gz").Value);

		var extra = (JsonObject) raw.DeepClone();
		extra["exec_start"] = "/bin/sh";
		Assert.Equal("Unknown keys in instance specification: exec_start", InstanceSpecification.Parse(extra).Failure!.Message);

		var missing = (JsonObject) raw.DeepClone();
		missing.Remove("package");
		missing.Remove("seed");
		Assert.Equal("Missing keys in instance specification: package, seed", InstanceSpecification.Parse(missing).Failure!.Message);
	}

	[Theory]
	[InlineData("version", "latest", "Invalid game version")]
	[InlineData("language", "русский", "Invalid server language")]
	[InlineData("playstyle", "godmode", "Invalid playstyle")]
	[InlineData("world_name", "", "Invalid world name")]
	[InlineData("server_name", "a\u0001b", "Invalid server name")]
	[InlineData("id", "../etc", "Unsafe instance id")]
	public void Демон_проверяет_поля_сам(string key, string value, string message)
	{
		var raw = ServerSpec.FromWizard(spec).Value.ToJson(StagedFile.Create("game-ab.tar.gz").Value);
		raw[key] = value;

		Assert.Equal(message, InstanceSpecification.Parse(raw).Failure!.Message);
	}

	[Fact]
	public void Демон_различает_булево_и_число()
	{
		var raw = ServerSpec.FromWizard(spec).Value.ToJson(StagedFile.Create("game-ab.tar.gz").Value);
		raw["pvp"] = 1;
		Assert.Equal("Whitelist and PvP must be booleans", InstanceSpecification.Parse(raw).Failure!.Message);

		raw["pvp"] = true;
		raw["max_players"] = 1001;
		Assert.Equal("Player count must be between 1 and 1000", InstanceSpecification.Parse(raw).Failure!.Message);
	}
}
