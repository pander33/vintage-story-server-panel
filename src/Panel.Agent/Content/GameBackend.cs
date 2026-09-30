using System.Globalization;
using Panel.Content.Domain;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Content;

/// <summary>
/// Программа сервера: проверка пакета, установка со снимком прежней версии, откат к снимку.
/// Операции <c>game-package-inspect</c>, <c>game-update-apply</c>, <c>game-update-rollback</c>
/// и <c>game-history</c>.
/// </summary>
/// <remarks>
/// <para>
/// Каталог заменяется двумя переименованиями: прежний уходит в
/// <c>.&lt;имя&gt;.old-&lt;pid&gt;</c>, распакованный встаёт на его место. Если второе
/// не удалось, первое откатывается. Распаковка идёт в соседний каталог на том же томе,
/// иначе переименование превратилось бы в копирование.
/// </para>
/// <para>
/// Остановлен ли сервер, проверяет вызывающий.
/// </para>
/// <para>
/// Пакет — <c>.tar.gz</c> (Linux) или <c>.zip</c> (Windows), см. <see cref="PackageArchive"/>.
/// Снимок прежней программы всегда <c>.tar.gz</c>: его пишет и читает только панель.
/// </para>
/// </remarks>
public sealed class GameBackend
{
	public const string SnapshotPrefix = "server-program-";

	private const string snapshotExtension = PackageArchive.TarExtension;

	private readonly StagingArea staging;
	private readonly TimeProvider clock;

	public GameBackend(StagingArea staging, TimeProvider clock)
	{
		ArgumentNullException.ThrowIfNull(staging);
		ArgumentNullException.ThrowIfNull(clock);
		this.staging = staging;
		this.clock = clock;
	}

	public async Task<Result<GamePackageInspectionPayload>> InspectAsync(StagedFile staged, CancellationToken ct)
	{
		var source = await PackageAsync(staged, ct).ConfigureAwait(false);
		if (!source.IsSuccess)
		{
			return Result.Fail<GamePackageInspectionPayload>(source.Failure!);
		}

		var layout = await JudgeAsync(source.Value, ct).ConfigureAwait(false);
		return layout.Map(value => new GamePackageInspectionPayload
		{
			Files = value.Files,
			UnpackedSize = value.UnpackedSize,
			Root = value.Root,
		});
	}

	/// <summary>
	/// Снимок прежней программы, его сверка, затем установка. Снимок пишется до любых изменений,
	/// иначе откатываться будет не к чему.
	/// </summary>
	public async Task<Result<GameUpdateAppliedPayload>> ApplyAsync(
		RegisteredServer server,
		StagedFile staged,
		CancellationToken ct)
	{
		var source = await PackageAsync(staged, ct).ConfigureAwait(false);
		if (!source.IsSuccess)
		{
			return Result.Fail<GameUpdateAppliedPayload>(source.Failure!);
		}

		var program = ProgramDirectory(server);
		if (!program.IsSuccess || server.BackupDirectory.Length == 0)
		{
			return Result.Fail<GameUpdateAppliedPayload>(
				program.Failure ?? Failure.Unavailable("Каталог резервных копий не задан в реестре"));
		}

		Directory.CreateDirectory(server.BackupDirectory);
		var name = SnapshotPrefix + GameFiles.BackupStamp(clock.GetLocalNow()) + snapshotExtension;
		var snapshot = Path.Combine(server.BackupDirectory, name);

		var written = await TreeArchive.WriteAsync(program.Value, snapshot, ct).ConfigureAwait(false);
		if (!written.IsSuccess)
		{
			return Result.Fail<GameUpdateAppliedPayload>(written.Failure!);
		}

		var verified = await TreeArchive.InspectAsync(snapshot, ct).ConfigureAwait(false);
		if (!verified.IsSuccess)
		{
			return Result.Fail<GameUpdateAppliedPayload>(verified.Failure!);
		}

		var installed = await InstallAsync(program.Value, source.Value, ct).ConfigureAwait(false);
		return installed.Map(layout => new GameUpdateAppliedPayload
		{
			Snapshot = name,
			Files = layout.Files,
			UnpackedSize = layout.UnpackedSize,
			Root = layout.Root,
		});
	}

	/// <summary>Вернуть снимок. Имя должно быть в перечне снимков.</summary>
	public async Task<Result<GameRollbackPayload>> RollbackAsync(
		RegisteredServer server,
		string name,
		CancellationToken ct)
	{
		var program = ProgramDirectory(server);
		if (!program.IsSuccess)
		{
			return Result.Fail<GameRollbackPayload>(program.Failure!);
		}

		var source = Snapshots(server).FirstOrDefault(info => info.Name == name);
		if (source is null)
		{
			return Failure.NotFound("Снимок программы сервера не найден");
		}

		var installed = await InstallAsync(program.Value, source.FullName, ct).ConfigureAwait(false);
		return installed.Map(_ => new GameRollbackPayload { Restored = name });
	}

	/// <summary>Двадцать последних снимков, новые первыми.</summary>
	public ProgramSnapshotsPayload History(RegisteredServer server) => new()
	{
		Backups =
		[
			.. Snapshots(server).Take(20).Select(info => new ProgramSnapshotPayload
			{
				Name = info.Name,
				Size = info.Length,
				Modified = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
			}),
		],
	};

	private static List<FileInfo> Snapshots(RegisteredServer server)
	{
		if (server.BackupDirectory.Length == 0 || !Directory.Exists(server.BackupDirectory))
		{
			return [];
		}

		return
		[
			.. Directory.EnumerateFiles(server.BackupDirectory, SnapshotPrefix + "*" + snapshotExtension)
				.Select(path => new FileInfo(path))
				.Where(info => info is { Exists: true, LinkTarget: null })
				.OrderByDescending(info => info.LastWriteTimeUtc),
		];
	}

	private async Task<Result<string>> PackageAsync(StagedFile staged, CancellationToken ct)
	{
		if (!staged.Name.StartsWith(StagedFile.Purpose.Game, StringComparison.Ordinal))
		{
			return Failure.Invalid("Временный файл не является пакетом игры");
		}

		var extension = PackageArchive.ExtensionOf(staged.Name);
		if (extension is null)
		{
			return Failure.Invalid("Пакет игры должен быть архивом .tar.gz или .zip");
		}

		return await staging.ResolveAsync(staged, extension, ct).ConfigureAwait(false);
	}

	private static async Task<Result<PackageLayout>> JudgeAsync(string archive, CancellationToken ct)
	{
		var entries = await PackageArchive.ListAsync(archive, ct).ConfigureAwait(false);
		return entries.IsSuccess ? ServerPackage.Judge(entries.Value) : Result.Fail<PackageLayout>(entries.Failure!);
	}

	private static Result<string> ProgramDirectory(RegisteredServer server)
	{
		var program = server.ProgramDirectory;
		var info = program.Length > 0 ? new DirectoryInfo(program) : null;

		// Родитель не может быть корнем тома («/» или «C:\»): рядом с программой создаются
		// временный и прежний каталоги.
		return info is { Exists: true, LinkTarget: null }
			   && info.Parent is { } parent
			   && !string.Equals(Path.GetPathRoot(parent.FullName), parent.FullName, StringComparison.OrdinalIgnoreCase)
			? Path.TrimEndingDirectorySeparator(info.FullName)
			: Failure.Unavailable("Каталог программы сервера небезопасен или отсутствует");
	}

	/// <summary>Проверить пакет, распаковать рядом, подменить каталог программы.</summary>
	private static async Task<Result<PackageLayout>> InstallAsync(string program, string source, CancellationToken ct)
	{
		var layout = await JudgeAsync(source, ct).ConfigureAwait(false);
		if (!layout.IsSuccess)
		{
			return layout;
		}

		var parent = Path.GetDirectoryName(program)!;
		var extractRoot = Path.Combine(parent, ".vs-game-new-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant());
		var old = Path.Combine(parent, $".{Path.GetFileName(program)}.old-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}");

		try
		{
			Directory.CreateDirectory(extractRoot);
			GameFiles.SetMode(extractRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

			var extracted = await PackageArchive.ExtractAsync(source, extractRoot, ct).ConfigureAwait(false);
			if (!extracted.IsSuccess)
			{
				return Result.Fail<PackageLayout>(extracted.Failure!);
			}

			var candidate = layout.Value.Root.Length > 0 ? Path.Combine(extractRoot, layout.Value.Root) : extractRoot;
			if (!File.Exists(Path.Combine(candidate, ServerPackage.ServerDll)))
			{
				return Failure.Invalid("В распакованном пакете нет VintagestoryServer.dll");
			}

			if (layout.Value.Root.Length == 0)
			{
				// Пакет без каталога верхнего уровня становится программой вместе с временным каталогом, у
				// которого права 0700, и игра не от root не вошла бы в свой каталог. Поэтому права берутся у
				// прежнего каталога программы.
				if (GameFiles.GetMode(program) is { } mode)
				{
					GameFiles.SetMode(candidate, mode);
				}
			}

			await GameFiles.MoveDirectoryAsync(program, old, ct).ConfigureAwait(false);
			try
			{
				await GameFiles.MoveDirectoryAsync(candidate, program, ct).ConfigureAwait(false);
			}
			catch
			{
				await GameFiles.MoveDirectoryAsync(old, program, CancellationToken.None).ConfigureAwait(false);
				throw;
			}

			TryDelete(old);
			return layout;
		}
		finally
		{
			TryDelete(extractRoot);
		}
	}

	private static void TryDelete(string directory)
	{
		try
		{
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// shutil.rmtree(..., ignore_errors=True): остаток уберётся при следующей установке.
		}
	}
}
