using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Panel.Agent.Transport;

/// <summary>
/// Демон на Windows: именованный канал вместо Unix-сокета (ADR-02, вариант B).
/// </summary>
/// <remarks>
/// Кто может подключиться, задаёт список доступа канала, и проверяет его ядро, как
/// <c>SO_PEERCRED</c> на Linux. Ни токен, ни TCP не нужны.
///
/// Список доступа: SYSTEM и администраторы — полный доступ; учётные записи из
/// <c>--allow-sid</c> (по умолчанию учётная запись демона) — чтение и запись без права
/// создавать экземпляры канала; вход по сети запрещён явно. Отдельной учётной записи на
/// Windows нет: демон и панель работают службами от LocalSystem (findings §69), а установщик
/// подключается из командной строки администратора. Первый экземпляр создаётся с
/// <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c>: если имя уже занято, демон не стартует, чтобы не
/// стать экземпляром чужого канала.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PipeAgentServer
{
	/// <summary>Префикс пути канала; панель пишет его в <c>PANEL_AGENT_SOCKET</c>.</summary>
	public const string PathPrefix = @"\\.\pipe\";

	private readonly string pipeName;
	private readonly PipeSecurity security;
	private readonly AgentConversation conversation;

	public PipeAgentServer(string pipe, IReadOnlyList<SecurityIdentifier> allowed, AgentDispatcher dispatcher)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pipe);
		ArgumentNullException.ThrowIfNull(allowed);
		ArgumentNullException.ThrowIfNull(dispatcher);

		if (allowed.Count == 0)
		{
			throw new ArgumentException("Не указано ни одной учётной записи, которой разрешён канал", nameof(allowed));
		}

		pipeName = NameOf(pipe);
		security = Security(allowed);
		// Создателю нужны последующие экземпляры канала, даже если он не SYSTEM и не
		// администратор. Клиентские --allow-sid по-прежнему получают только ReadWrite.
		security.AddAccessRule(new PipeAccessRule(
			WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
		conversation = new AgentConversation(dispatcher);
	}

	/// <summary>Имя канала без префикса; демон принимает обе формы.</summary>
	public static string NameOf(string pipe) =>
		pipe.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) ? pipe[PathPrefix.Length..] : pipe;

	/// <summary>
	/// Учётная запись по SID (<c>S-1-5-19</c>) или имени (<c>NT AUTHORITY\LocalService</c>).
	/// </summary>
	public static SecurityIdentifier Resolve(string account)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(account);

		return account.StartsWith("S-", StringComparison.OrdinalIgnoreCase)
			? new SecurityIdentifier(account)
			: (SecurityIdentifier) new NTAccount(account).Translate(typeof(SecurityIdentifier));
	}

	public static PipeSecurity Security(IReadOnlyList<SecurityIdentifier> allowed)
	{
		ArgumentNullException.ThrowIfNull(allowed);

		var security = new PipeSecurity();
		security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

		security.AddAccessRule(new PipeAccessRule(
			new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
		security.AddAccessRule(new PipeAccessRule(
			new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
		security.AddAccessRule(new PipeAccessRule(
			new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

		foreach (var account in allowed)
		{
			security.AddAccessRule(new PipeAccessRule(account, PipeAccessRights.ReadWrite, AccessControlType.Allow));
		}

		return security;
	}

	public async Task RunAsync(CancellationToken ct)
	{
		var first = true;
		while (!ct.IsCancellationRequested)
		{
			var pipe = Create(first);
			first = false;

			try
			{
				await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
			}
			catch
			{
				await pipe.DisposeAsync().ConfigureAwait(false);
				throw;
			}

			_ = ServeAsync(pipe, ct);
		}
	}

	private NamedPipeServerStream Create(bool first) =>
		NamedPipeServerStreamAcl.Create(
			pipeName,
			PipeDirection.InOut,
			NamedPipeServerStream.MaxAllowedServerInstances,
			PipeTransmissionMode.Byte,
			PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
			inBufferSize: 0,
			outBufferSize: 0,
			security);

	private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
	{
		await using (pipe.ConfigureAwait(false))
		{
			try
			{
				await conversation.RunAsync(pipe, ct).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// Демон останавливается.
			}
		}
	}
}
