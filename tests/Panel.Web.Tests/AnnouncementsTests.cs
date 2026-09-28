using System.Text.Json.Nodes;
using Panel.Configuration.Domain;
using Panel.Configuration.Ports;
using Panel.Contracts.Agent;
using Panel.Contracts.Configuration;
using Panel.Contracts.ServerCommands;
using Panel.Runtime.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;

namespace Panel.Web.Tests;

/// <summary>
/// Объявление читают игроки, поэтому язык берётся из <c>ServerLanguage</c> сервера, а не из
/// интерфейса панели.
/// </summary>
public sealed class AnnouncementsTests
{
	private static readonly InstanceId main = InstanceId.Parse("main");

	[Theory]
	[InlineData("ru", "/announce Плановое обслуживание через 5 мин.")]
	[InlineData("ru-RU", "/announce Плановое обслуживание через 5 мин.")]
	[InlineData("en", "/announce Scheduled maintenance in 5 min.")]
	[InlineData("de", "/announce Scheduled maintenance in 5 min.")]
	[InlineData(null, "/announce Scheduled maintenance in 5 min.")]
	public async Task Объявление_на_языке_сервера(string? language, string expected)
	{
		var players = new RecordingPlayers();
		var announcements = new Announcements(players, new ServerLanguage(language));

		var sent = await announcements.SendAsync(main, Phrase.Format($"Плановое обслуживание через {5} мин."), CancellationToken.None);

		Assert.True(sent.IsSuccess);
		Assert.Equal([expected], players.Lines);
	}

	private sealed class RecordingPlayers : IPlayerCommands
	{
		public List<string> Lines { get; } = [];

		public Task<Result<PlayerActionPayload>> SendAsync(InstanceId instance, PlayerCommand command, CancellationToken ct)
		{
			Lines.Add(command.Render(string.Empty));
			return Task.FromResult(Result.Ok(new PlayerActionPayload { Action = command.Action }));
		}
	}
}

/// <summary><c>serverconfig.json</c> с одним <c>ServerLanguage</c>; <see langword="null"/> — файл не читается.</summary>
internal sealed class ServerLanguage(string? language) : IServerConfigStore
{
	public Task<Result<ServerConfigFilePayload>> ReadAsync(InstanceId instance, CancellationToken ct) =>
		Task.FromResult(language is null
			? Result<ServerConfigFilePayload>.Fail(Failure.Unavailable("Демон недоступен"))
			: Result.Ok(new ServerConfigFilePayload { Config = new JsonObject { ["ServerLanguage"] = language } }));

	public Task<Result<ConfigWritePayload>> WriteAsync(InstanceId instance, StagedFile staged, CancellationToken ct) => throw new NotSupportedException();

	public Task<Result<ConfigBackupsPayload>> ListBackupsAsync(InstanceId instance, CancellationToken ct) => throw new NotSupportedException();

	public Task<Result<ConfigRollbackPayload>> RollbackAsync(InstanceId instance, ConfigBackupName backup, CancellationToken ct) => throw new NotSupportedException();
}
