using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Maintenance.Domain;

namespace Panel.Maintenance.Infrastructure;

/// <summary>
/// Аренда обслуживания в файле рядом с <c>accounts.json</c>, общая для всей панели.
/// </summary>
/// <remarks>
/// Захват — создание файла с <see cref="FileMode.CreateNew"/>: второму создающему система
/// откажет сама, отдельная блокировка не нужна. Существующий файл перечитывается: если его
/// владелец мёртв, файл удаляется и захват повторяется один раз. Методы синхронные: они
/// вызываются под замком координатора, а файл маленький.
/// </remarks>
public sealed class FileLeaseStore
{
	private readonly string path;

	public FileLeaseStore(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		this.path = path;
	}

	public string Path => path;

	/// <summary>Записанная аренда; для нечитаемого файла <see cref="Unreadable"/>.</summary>
	public LeaseRecord Read()
	{
		string text;
		try
		{
			text = File.ReadAllText(path, Encoding.UTF8);
		}
		catch (FileNotFoundException)
		{
			return LeaseRecord.None;
		}
		catch (DirectoryNotFoundException)
		{
			return LeaseRecord.None;
		}

		try
		{
			return MaintenanceLease.FromJson(JsonNode.Parse(text)) is { } lease ? new LeaseRecord(lease) : LeaseRecord.Unreadable;
		}
		catch (JsonException)
		{
			return LeaseRecord.Unreadable;
		}
	}

	/// <summary>
	/// Взять аренду. <paramref name="abandoned"/> решает, можно ли убрать чужую. Нечитаемый файл
	/// считается брошенным: живая панель такой не оставит.
	/// </summary>
	public bool TryAcquire(MaintenanceLease lease, Func<MaintenanceLease, bool> abandoned)
	{
		ArgumentNullException.ThrowIfNull(lease);
		ArgumentNullException.ThrowIfNull(abandoned);

		for (var attempt = 0; attempt < 2; attempt++)
		{
			if (TryCreate(lease))
			{
				return true;
			}

			var existing = Read();
			if (existing.Lease is { } held && !abandoned(held))
			{
				return false;
			}

			if (existing.Exists)
			{
				Delete();
			}
		}

		return false;
	}

	/// <summary>Отпустить свою аренду. Чужую не трогать: её мог взять следующий владелец.</summary>
	public void Release(Guid id)
	{
		if (Read().Lease is { } held && held.Id == id)
		{
			Delete();
		}
	}

	public void Delete()
	{
		try
		{
			File.Delete(path);
		}
		catch (DirectoryNotFoundException)
		{
			// Файла уже нет.
		}
	}

	private bool TryCreate(MaintenanceLease lease)
	{
		var directory = System.IO.Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}

		try
		{
			using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
			var bytes = Encoding.UTF8.GetBytes(lease.ToJson().ToJsonString() + "\n");
			file.Write(bytes);
			file.Flush(flushToDisk: true);
		}
		catch (IOException) when (File.Exists(path))
		{
			return false;
		}

		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}

		return true;
	}
}

/// <summary>Что лежит в файле аренды.</summary>
public sealed record LeaseRecord(MaintenanceLease? Lease, bool Exists = true)
{
	public static readonly LeaseRecord None = new(Lease: null, Exists: false);

	public static readonly LeaseRecord Unreadable = new(Lease: null);
}
