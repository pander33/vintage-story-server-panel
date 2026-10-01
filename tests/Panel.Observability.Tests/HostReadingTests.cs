using Panel.Observability.Domain;
using Panel.Observability.Infrastructure;

namespace Panel.Observability.Tests;

/// <summary>
/// Сводка о машине панели: разбор <c>/proc</c> и замер на той машине, где идут тесты.
/// </summary>
public sealed class HostReadingTests
{
	[Fact]
	public void Процессор_простой_это_idle_и_iowait()
	{
		var times = CpuTimes.ParseProcStat("cpu  100 20 30 400 50 6 4 10 7 0\ncpu0 1 2 3 4 5 6 7 8 0 0\n");

		// guest (7) уже входит в user и второй раз не складывается.
		Assert.Equal(new CpuTimes(170, 620), times);
	}

	[Fact]
	public void Bind_монтирование_каталога_состояния_это_тот_же_корневой_том()
	{
		// Так mountinfo службы панели выглядит при ProtectSystem=strict и ReadWritePaths=/var/lib/vs-panel.
		const string mountinfo = """
			25 1 8:1 / / ro,relatime shared:1 - ext4 /dev/sda1 rw
			26 25 0:22 / /proc rw,nosuid shared:2 - proc proc rw
			40 25 8:1 /var/lib/vs-panel /var/lib/vs-panel rw,relatime shared:1 - ext4 /dev/sda1 rw
			41 25 8:17 / /srv rw,relatime shared:3 - ext4 /dev/sdb1 rw
			42 41 8:17 /games/my\040world /srv/my\040world rw,relatime shared:3 - ext4 /dev/sdb1 rw
			43 25 0:50 /x /orphan rw - tmpfs tmpfs rw
			""";

		var origins = HostProbe.ParseMountinfo(mountinfo);

		Assert.Equal("/", origins["/var/lib/vs-panel"]);
		Assert.Equal("/srv", origins["/srv/my world"]);
		Assert.Equal(2, origins.Count);
	}

	[Theory]
	[InlineData("")]
	[InlineData("intr 1 2 3")]
	[InlineData("cpu  1 2 x 4")]
	public void Нечитаемая_строка_процессора_не_замер(string text) =>
		Assert.Null(CpuTimes.ParseProcStat(text));

	[Fact]
	public void Загрузка_разница_двух_замеров()
	{
		Assert.Equal(25.0, CpuTimes.Percent(new CpuTimes(100, 1000), new CpuTimes(150, 1200)));
		Assert.Null(CpuTimes.Percent(new CpuTimes(100, 1000), new CpuTimes(100, 1000)));
	}

	[Fact]
	public void Доступная_память_из_MemAvailable_а_не_MemFree()
	{
		var memory = HostMemory.ParseMeminfo(
			"MemTotal:       16318412 kB\nMemFree:          512000 kB\nMemAvailable:    8159206 kB\nSwapTotal:       2097148 kB\nSwapFree:        1048574 kB\n");

		Assert.NotNull(memory);
		Assert.Equal(16318412L * 1024, memory.Value.Total);
		Assert.Equal(8159206L * 1024, memory.Value.Available);
		Assert.Equal(8159206L * 1024, memory.Value.Used);
		Assert.Equal(1048574L * 1024, memory.Value.SwapUsed);
	}

	[Fact]
	public void Без_MemAvailable_память_неизвестна() =>
		Assert.Null(HostMemory.ParseMeminfo("MemTotal: 1024 kB\nMemFree: 512 kB\n"));

	[Fact]
	public void Средняя_нагрузка_три_числа() =>
		Assert.Equal(new LoadAverage(0.52, 0.4, 0.35), LoadAverage.ParseLoadavg("0.52 0.40 0.35 1/234 5678\n"));

	[Theory]
	[InlineData("/srv/vs/data", "/srv", false, true)]
	[InlineData("/srvx/data", "/srv", false, false)]
	[InlineData("/srv/vs/data", "/", false, true)]
	[InlineData(@"D:\Games\vs", @"d:\", true, true)]
	[InlineData(@"D:\Games\vs", @"C:\", true, false)]
	public void Путь_на_томе(string path, string mount, bool windows, bool expected) =>
		Assert.Equal(expected, HostProbe.IsUnder(path, mount, windows));

	[Fact]
	public void Том_временного_каталога_найден()
	{
		var space = HostProbe.VolumeOf(Path.GetTempPath(), DriveInfo.GetDrives());

		Assert.NotNull(space);
		Assert.True(space.Value.Total > 0);
		Assert.InRange(space.Value.Free, 0, space.Value.Total);
	}

	[Fact]
	public async Task Замер_этой_машины()
	{
		var probe = new HostProbe(TimeProvider.System);

		var cpu = await probe.CpuPercentAsync(CancellationToken.None);
		var memory = HostProbe.Memory();

		Assert.NotNull(cpu);
		Assert.InRange(cpu.Value, 0, 100);
		Assert.NotNull(memory);
		Assert.InRange(memory.Value.Available, 0, memory.Value.Total);
		Assert.NotEmpty(probe.Platform);
	}
}
