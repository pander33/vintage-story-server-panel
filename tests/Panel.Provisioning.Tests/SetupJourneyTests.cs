using System.Text.Json.Nodes;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Provisioning.Tests;

/// <summary>
/// Мастер установки на уровне агрегата, включая правила из заметки «Мастер установки —
/// диаграмма состояний SetupJourney».
/// </summary>
public sealed class SetupJourneyTests
{
	private static readonly InstanceId creative = InstanceId.Parse("creative");
	private static readonly IReadOnlySet<InstanceId> nothing = new HashSet<InstanceId>();
	private static readonly IReadOnlySet<InstanceId> withCreative = new HashSet<InstanceId> { creative };

	private static ServerSpec Spec => ServerSpec.FromWizard(new ServerAnswers
	{
		Id = "creative",
		Version = "1.22.7",
		Port = 42421,
		MaxPlayers = 16,
		Pvp = true,
		Language = "ru",
		Playstyle = "creativebuilding",
	}).Value;

	private static AccessSettings Access(int port = 8080) => AccessSettings.Create("127.0.0.1", port, "localhost", false).Value;

	private static SetupJourney Claimed()
	{
		var journey = SetupJourney.FromJson(null);
		Assert.False(journey.Claim(nothing, 100).Value);
		return journey;
	}

	private static SetupJourney AtServer()
	{
		var journey = Claimed();
		journey.RememberAccess(Access());
		journey.RememberMachineCheck();
		journey.RememberVersion(ChosenVersion.Create("1.22.7")!.Value);
		return journey;
	}

	[Fact]
	public void Мастер_помнит_ответы_и_не_ходит_назад()
	{
		var journey = SetupJourney.FromJson(null);
		Assert.Equal(SetupStep.Claim, journey.Step);

		journey.Claim(nothing, 100);
		journey.RememberAccess(Access());
		Assert.Equal(SetupStep.Preflight, journey.Step);

		journey.RememberMachineCheck();
		journey.RememberVersion(ChosenVersion.Create("1.22.7")!.Value);
		journey.RememberServer(Spec);
		Assert.Equal(SetupStep.Create, journey.Step);

		// Возврат на предыдущий шаг в интерфейсе не откатывает прогресс.
		Assert.True(journey.RememberAccess(Access(9000)).IsSuccess);
		Assert.Equal(SetupStep.Create, journey.Step);
		Assert.Equal("9000", (string?) journey.Draft(SetupStep.Access)["PANEL_PORT"]);

		var restored = SetupJourney.FromJson(journey.ToJson());
		Assert.Equal("creative", (string?) restored.Draft(SetupStep.Server)["id"]);
		Assert.Equal(SetupStep.Create, restored.Step);
	}

	[Fact]
	public void Завершённый_мастер_остаётся_завершённым()
	{
		var journey = AtServer();
		journey.RememberServer(Spec);
		Assert.True(journey.Finish(creative, withCreative, 500).IsSuccess);

		var restored = SetupJourney.FromJson(journey.ToJson());
		Assert.True(restored.IsCompleted);
		Assert.Equal(SetupStep.Done, restored.Step);
		Assert.Equal("creative", (string?) restored.ToJson()["instance"]);
		Assert.Equal(500, restored.CompletedAt);

		// Из завершённого мастера никакой переход невозможен.
		Assert.False(restored.Claim(withCreative, 600).IsSuccess);
		Assert.False(restored.RememberAccess(Access()).IsSuccess);
		Assert.False(restored.RememberMachineCheck().IsSuccess);
		Assert.False(restored.RememberServer(Spec).IsSuccess);
		Assert.False(restored.BeginCreation(Guid.NewGuid()).IsSuccess);
		Assert.False(restored.Finish(creative, withCreative, 600).IsSuccess);
		Assert.False(restored.AdministratorPresent());
	}

	[Fact]
	public void До_захвата_ничего_не_настраивается()
	{
		var journey = SetupJourney.FromJson(null);

		Assert.False(journey.RememberAccess(Access()).IsSuccess);
		Assert.False(journey.RememberMachineCheck().IsSuccess);
		Assert.Equal(SetupStep.Claim, journey.Step);
	}

	[Fact]
	public void Сервер_созданный_установщиком_закрывает_мастер_при_захвате()
	{
		var journey = SetupJourney.FromJson(new JsonObject { ["prepared"] = "creative" });

		Assert.True(journey.Claim(withCreative, 100).Value);
		Assert.True(journey.IsCompleted);
		Assert.Equal("Сервер creative создан установщиком", journey.ToJson()["detail"].Said());
	}

	[Fact]
	public void Подготовленный_сервер_которого_нет_в_реестре_мастер_не_закрывает()
	{
		var journey = SetupJourney.FromJson(new JsonObject { ["prepared"] = "creative" });

		Assert.False(journey.Claim(nothing, 100).Value);
		Assert.False(journey.IsCompleted);
		Assert.Equal(SetupStep.Access, journey.Step);
	}

	[Fact]
	public void Проверка_версии_не_перепрыгивает_проверку_машины()
	{
		// §4.1: непустая версия на шаге «preflight» не переставляет указатель на «server».
		var journey = Claimed();
		journey.RememberAccess(Access());

		Assert.False(journey.RememberVersion(ChosenVersion.Create("1.22.7")!.Value).IsSuccess);
		Assert.Equal(SetupStep.Preflight, journey.Step);
	}

	[Fact]
	public void Настройки_сервера_не_принимаются_до_выбора_версии()
	{
		var journey = Claimed();
		journey.RememberAccess(Access());
		journey.RememberMachineCheck();

		Assert.False(journey.RememberServer(Spec).IsSuccess);
		Assert.Equal(SetupStep.Version, journey.Step);
	}

	[Fact]
	public void Создание_без_записанных_настроек_отвергается()
	{
		var journey = AtServer();

		Assert.Equal("Сначала заполните настройки сервера", journey.BeginCreation(Guid.NewGuid()).Failure!.Message);

		journey.RememberServer(Spec);
		Assert.Equal(Spec, journey.SpecificationToCreate().Value);
	}

	[Fact]
	public void Перезапуск_панели_во_время_создания_возвращает_к_той_же_операции()
	{
		// §4.2: идентификатор операции хранится в состоянии мастера, а не берётся последним из снимка.
		var operation = Guid.NewGuid();
		var journey = AtServer();
		journey.RememberServer(Spec);
		journey.BeginCreation(operation);

		var restored = SetupJourney.FromJson(journey.ToJson());

		Assert.Equal(SetupStep.Create, restored.Step);
		Assert.Equal(operation, restored.Operation);
	}

	[Fact]
	public void Завершение_требует_зарегистрированного_сервера()
	{
		var journey = AtServer();
		journey.RememberServer(Spec);

		var refused = journey.Finish(creative, nothing, 500);

		Assert.Equal("Сервер ещё не зарегистрирован: дождитесь окончания создания", refused.Failure!.Message);
		Assert.False(journey.IsCompleted);
	}

	[Fact]
	public void Мастер_закрывается_без_сервера_после_доступа()
	{
		var journey = Claimed();
		journey.RememberAccess(Access());

		Assert.True(journey.FinishWithoutServer(500).IsSuccess);

		var restored = SetupJourney.FromJson(journey.ToJson());
		Assert.True(restored.IsCompleted);
		Assert.Equal(SetupStep.Done, restored.Step);
		Assert.Null(restored.ToJson()["instance"]);
		Assert.Equal(500, restored.CompletedAt);
		Assert.False(restored.FinishWithoutServer(600).IsSuccess);
		Assert.False(restored.BeginCreation(Guid.NewGuid()).IsSuccess);
	}

	/// <summary>Без записанного доступа закрытый мастер оставил бы панель на адресе, который никто не выбирал.</summary>
	[Fact]
	public void Без_доступа_пропустить_сервер_нельзя()
	{
		var journey = Claimed();

		var refused = journey.FinishWithoutServer(500);

		Assert.Equal("Сначала настройте доступ к панели", refused.Failure!.Message);
		Assert.False(journey.IsCompleted);
		Assert.False(SetupJourney.FromJson(null).FinishWithoutServer(500).IsSuccess);
	}

	[Fact]
	public void Администратор_старше_мастера_продолжает_с_доступа()
	{
		var journey = SetupJourney.FromJson(null);

		Assert.True(journey.AdministratorPresent());
		Assert.Equal(SetupStep.Access, journey.Step);
		Assert.False(journey.AdministratorPresent());
	}

	[Fact]
	public void Настроенная_до_мастера_панель_принимается_без_вопросов()
	{
		Assert.False(SetupJourney.FromJson(null).Adopt(creative, hasAdministrator: false, withCreative, 1));
		Assert.False(SetupJourney.FromJson(null).Adopt(creative, hasAdministrator: true, nothing, 1));

		var journey = SetupJourney.FromJson(null);
		Assert.True(journey.Adopt(creative, hasAdministrator: true, withCreative, 1));
		Assert.Equal("Панель была настроена до появления мастера установки", journey.ToJson()["detail"].Said());
	}

	[Fact]
	public void Незнакомые_ключи_и_неизвестный_шаг_переживают_чтение()
	{
		var journey = SetupJourney.FromJson(new JsonObject { ["step"] = "somewhere", ["future"] = 1 });

		Assert.Equal(SetupStep.Claim, journey.Step);
		Assert.Equal(1, (int) journey.ToJson()["future"]!);
		Assert.False(journey.IsCompleted);
	}

	[Fact]
	public void Токен_захвата_сравнивается_целиком()
	{
		var token = ClaimToken.Create("  secret-token\n")!;

		Assert.True(token.Accepts(OfferedToken.Create(" secret-token ")));
		Assert.False(token.Accepts(OfferedToken.Create("secret")));
		Assert.False(token.Accepts(OfferedToken.Create("secret-token-longer")));
		Assert.False(token.Accepts(OfferedToken.Create("секрет")));
		Assert.False(token.Accepts(OfferedToken.Create(null)));
		Assert.Null(ClaimToken.Create("  \n"));
	}

	/// <summary>
	/// Установщик создал сервер до появления администратора: ответы записаны как ответы мастера,
	/// указатель не сдвинут, а захват по ссылке закрывает мастер (INV-45).
	/// </summary>
	[Fact]
	public void Сервер_установщика_закрывает_мастер_при_захвате()
	{
		var journey = SetupJourney.FromJson(null);

		Assert.True(journey.PrepareByInstaller(Spec).IsSuccess);

		Assert.Equal(SetupStep.Claim, journey.Step);
		Assert.Equal(creative, journey.Prepared);
		Assert.Equal("1.22.7", journey.Draft(SetupStep.Version)["version"]!.GetValue<string>());
		Assert.Equal("creative", journey.Draft(SetupStep.Server)["id"]!.GetValue<string>());
		Assert.True(journey.Claim(withCreative, 700).Value);
		Assert.True(journey.IsCompleted);
	}

	[Fact]
	public void Завершённый_мастер_не_принимает_сервер_установщика()
	{
		var journey = SetupJourney.FromJson(new JsonObject { ["completed"] = true, ["step"] = "done" });

		Assert.False(journey.PrepareByInstaller(Spec).IsSuccess);
		Assert.Null(journey.Prepared);
	}
}
