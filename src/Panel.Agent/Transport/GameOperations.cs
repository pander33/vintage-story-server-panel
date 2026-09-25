using System.Text.Json;
using System.Text.RegularExpressions;
using Panel.Agent.Content;
using Panel.Agent.Runtime;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Fleet.Infrastructure;
using Panel.SharedKernel;

namespace Panel.Agent.Transport;

/// <summary>
/// Операции над программой сервера и его версией в реестре. Каждый аргумент проверяется
/// здесь.
/// </summary>
/// <remarks>
/// Установка и откат требуют остановленного сервера: при замене каталога работающего
/// процесса часть сервера оказалась бы новой. Останавливает сервер панель, она же
/// предупреждает игроков и снимает копию данных.
/// </remarks>
public sealed partial class GameOperations : IServerOperations
{
	private readonly GameBackend game;
	private readonly IServiceBackend service;
	private readonly RegistryEditor editor;

	public GameOperations(GameBackend game, IServiceBackend service, RegistryEditor editor)
	{
		ArgumentNullException.ThrowIfNull(game);
		ArgumentNullException.ThrowIfNull(service);
		ArgumentNullException.ThrowIfNull(editor);
		this.game = game;
		this.service = service;
		this.editor = editor;
	}

	public static bool Handles(string op) => op is
		AgentProtocol.OpGamePackageInspect or AgentProtocol.OpGameHistory or AgentProtocol.OpGameUpdateApply
		or AgentProtocol.OpGameUpdateRollback or AgentProtocol.OpPanelGameVersion;

	bool IServerOperations.Serves(string op) => Handles(op);

	public async Task<AgentReply> ExecuteAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		switch (request.Op)
		{
			case AgentProtocol.OpGameHistory:
				return Reply(Result.Ok(game.History(server)));

			case AgentProtocol.OpGamePackageInspect:
				{
					var staged = StagedFile.Create(Argument(request, 0));
					return staged.IsSuccess
						? Reply(await game.InspectAsync(staged.Value, ct).ConfigureAwait(false))
						: Failed(staged.Failure!);
				}

			case AgentProtocol.OpGameUpdateApply:
				{
					var staged = StagedFile.Create(Argument(request, 0));
					if (!staged.IsSuccess)
					{
						return Failed(staged.Failure!);
					}

					if (await service.IsActiveAsync(server, ct).ConfigureAwait(false))
					{
						return Failed(Failure.Conflict("Остановите сервер Vintage Story перед заменой программы"));
					}

					return Reply(await game.ApplyAsync(server, staged.Value, ct).ConfigureAwait(false));
				}

			case AgentProtocol.OpGameUpdateRollback:
				{
					var name = (Argument(request, 0) ?? string.Empty).Trim();
					if (!PlainFileName.IsPlain(name) || name.Length is 0 or > 255)
					{
						return Failed(Failure.Invalid("Недопустимое имя снимка"));
					}

					if (await service.IsActiveAsync(server, ct).ConfigureAwait(false))
					{
						return Failed(Failure.Conflict("Остановите сервер Vintage Story перед заменой программы"));
					}

					return Reply(await game.RollbackAsync(server, name, ct).ConfigureAwait(false));
				}

			case AgentProtocol.OpPanelGameVersion:
				return await RecordVersionAsync(request, server, ct).ConfigureAwait(false);

			default:
				return AgentReply.Failure("op", $"Неизвестная операция: {request.Op}");
		}
	}

	/// <summary>
	/// Версия записывается обновлённому серверу, а не всей панели. Реестр версии 1 записи не
	/// принимает, <see cref="RegistryEditor"/> вернёт отказ с объяснением.
	/// </summary>
	/// <remarks>
	/// В <c>PANEL_GAME_VERSION</c> файла окружения версия не пишется: правка этого файла от root —
	/// отдельная привилегированная операция, и она относится к настройкам панели, а не к
	/// обновлению игры.
	/// </remarks>
	private async Task<AgentReply> RecordVersionAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var version = Argument(request, 0) ?? string.Empty;
		if (!GameVersionPattern().IsMatch(version))
		{
			return Failed(Failure.Invalid("Недопустимая версия игры"));
		}

		var recorded = await editor.EditAsync(document =>
		{
			if (document.Instances[server.Id.Value] is not System.Text.Json.Nodes.JsonObject entry)
			{
				return Failure.NotFound($"Неизвестный сервер {server.Id}");
			}

			entry["game_version"] = version;
			return Result.Ok(new GameVersionRecordedPayload { GameVersion = version, PanelRestartRequired = false });
		}, ct).ConfigureAwait(false);

		return Reply(recorded);
	}

	private static string? Argument(AgentRequest request, int index) =>
		index < request.Args.Count ? request.Args[index] : null;

	private static AgentReply Reply<T>(Result<T> result) =>
		result.IsSuccess
			? AgentReply.Success(JsonSerializer.SerializeToNode(result.Value, PanelJson.Options))
			: Failed(result.Failure!);

	private static AgentReply Failed(Failure failure) => AgentReply.Failure(
		failure.Code switch
		{
			FailureCode.NotFound => "notfound",
			FailureCode.Invalid => "invalid",
			FailureCode.Conflict => "conflict",
			_ => "unavailable",
		},
		failure.Message);

	/// <summary>Номер версии игры.</summary>
	[GeneratedRegex(@"\A\d{1,3}\.\d{1,3}\.\d{1,4}(?:-[A-Za-z0-9.]{1,16})?\z")]
	private static partial Regex GameVersionPattern();
}
