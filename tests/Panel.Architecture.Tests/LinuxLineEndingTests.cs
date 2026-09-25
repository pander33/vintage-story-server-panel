namespace Panel.Architecture.Tests;

/// <summary>
/// Файлы, которые исполняет или разбирает Linux, должны быть с LF (INV-50). Дерево собирается
/// на Windows и копируется на сервер как есть, а <c>.gitattributes</c> защищает репозиторий, но
/// не рабочую копию. С CR sh пишет <c>$'\r': команда не найдена</c>, а systemd считает CR
/// частью значения.
/// </summary>
public sealed class LinuxLineEndingTests
{
	public static TheoryData<string> Files()
	{
		var root = Repository();
		var files = new TheoryData<string>();
		foreach (var path in Directory.GetFiles(Path.Combine(root, "deploy"))
			.Concat(Directory.GetFiles(Path.Combine(root, "scripts"), "*.sh", SearchOption.AllDirectories)))
		{
			files.Add(Path.GetRelativePath(root, path));
		}

		return files;
	}

	[Theory]
	[MemberData(nameof(Files))]
	public void Файл_для_Linux_без_CR(string relative)
	{
		var text = File.ReadAllText(Path.Combine(Repository(), relative));

		Assert.False(text.Contains('\r', StringComparison.Ordinal), $"{relative} содержит CR");
	}

	/// <summary><c>.gitattributes</c> не должен требовать CRLF для файлов, которые разбирает Linux.</summary>
	[Fact]
	public void Gitattributes_не_требует_CRLF_для_файлов_Linux()
	{
		var rules = File.ReadAllLines(Path.Combine(Repository(), ".gitattributes"));

		Assert.Contains(rules, rule => rule.StartsWith("*.service", StringComparison.Ordinal) && rule.Contains("eol=lf", StringComparison.Ordinal));
		Assert.Contains(rules, rule => rule.StartsWith("*.sh", StringComparison.Ordinal) && rule.Contains("eol=lf", StringComparison.Ordinal));
		Assert.Contains(rules, rule => rule.StartsWith("deploy/vs-console-wrapper", StringComparison.Ordinal) && rule.Contains("eol=lf", StringComparison.Ordinal));
	}

	private static string Repository()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VintageStoryPanel.slnx")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName ?? throw new InvalidOperationException("Корень репозитория не найден");
	}
}
