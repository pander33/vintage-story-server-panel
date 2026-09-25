using System.Reflection;

namespace Panel.Architecture.Tests;

/// <summary>
/// Граф ссылок между сборками. Правило ADR-01: контекст не ссылается на другой контекст,
/// только на published language и разделяемое ядро.
/// </summary>
public sealed class ReferenceGraphTests
{
	/// <summary>Сборки контекстов: предметная область без платформенного кода.</summary>
	private static readonly string[] contexts =
	[
		"Panel.Access",
		"Panel.Catalogues",
		"Panel.Configuration",
		"Panel.Content",
		"Panel.Fleet",
		"Panel.Maintenance",
		"Panel.Observability",
		"Panel.Provisioning",
		"Panel.Runtime",
	];

	/// <summary>На что контексту ссылаться можно.</summary>
	private static readonly string[] allowed =
	[
		"Panel.SharedKernel",
		"Panel.Contracts",
	];

	[Theory]
	[InlineData("Panel.Access")]
	[InlineData("Panel.Catalogues")]
	[InlineData("Panel.Configuration")]
	[InlineData("Panel.Content")]
	[InlineData("Panel.Fleet")]
	[InlineData("Panel.Maintenance")]
	[InlineData("Panel.Observability")]
	[InlineData("Panel.Provisioning")]
	[InlineData("Panel.Runtime")]
	public void Контекст_не_ссылается_на_другой_контекст(string context)
	{
		var offenders = Assemblies.References(context)
			.Where(reference => contexts.Contains(reference) && reference != context)
			.ToArray();

		Assert.True(
			offenders.Length == 0,
			$"{context} ссылается на другой контекст: {string.Join(", ", offenders)}");
	}

	[Theory]
	[InlineData("Panel.Access")]
	[InlineData("Panel.Catalogues")]
	[InlineData("Panel.Configuration")]
	[InlineData("Panel.Content")]
	[InlineData("Panel.Fleet")]
	[InlineData("Panel.Maintenance")]
	[InlineData("Panel.Observability")]
	[InlineData("Panel.Provisioning")]
	[InlineData("Panel.Runtime")]
	public void Контекст_ссылается_только_на_ядро_и_контракты(string context)
	{
		var offenders = Assemblies.References(context)
			.Where(reference => reference.StartsWith("Panel.", StringComparison.Ordinal))
			.Where(reference => !allowed.Contains(reference))
			.ToArray();

		Assert.True(
			offenders.Length == 0,
			$"{context} ссылается на недопустимое: {string.Join(", ", offenders)}");
	}

	[Fact]
	public void Разделяемое_ядро_ни_на_что_не_ссылается()
	{
		var offenders = Assemblies.References("Panel.SharedKernel")
			.Where(reference => reference.StartsWith("Panel.", StringComparison.Ordinal))
			.ToArray();

		Assert.True(offenders.Length == 0, string.Join(", ", offenders));
	}

	/// <summary>
	/// Контракты — published language и не должны знать ни одного контекста.
	/// </summary>
	[Fact]
	public void Контракты_не_знают_контекстов()
	{
		var offenders = Assemblies.References("Panel.Contracts")
			.Where(reference => contexts.Contains(reference))
			.ToArray();

		Assert.True(offenders.Length == 0, string.Join(", ", offenders));
	}

	/// <summary>
	/// Установщик и панель — разные процессы с разными правами: установщик работает от root и
	/// вызывает панель командой (<c>vs-panel-web manage</c>), а не через ссылку на сборку. Ссылка
	/// в любую сторону затащила бы в процесс root веб-граф или в панель — код, который пишет
	/// юниты systemd.
	/// </summary>
	[Fact]
	public void Установщик_и_панель_не_ссылаются_друг_на_друга()
	{
		Assert.DoesNotContain("Panel.Web", Assemblies.References("Panel.Cli"));
		Assert.DoesNotContain("Panel.Agent", Assemblies.References("Panel.Cli"));
		Assert.DoesNotContain("Panel.Agent.Client", Assemblies.References("Panel.Cli"));
		Assert.DoesNotContain("Panel.Cli", Assemblies.References("Panel.Web"));
		Assert.DoesNotContain("Panel.Cli", Assemblies.References("Panel.Agent.Client"));
	}

	/// <summary>
	/// Домен не знает о транспорте привилегированной стороны. Клиент демона — адаптер порта, и
	/// зависимость направлена только к домену.
	/// </summary>
	[Fact]
	public void Домен_не_знает_о_клиенте_демона()
	{
		foreach (var context in contexts)
		{
			Assert.DoesNotContain("Panel.Agent.Client", Assemblies.References(context));
			Assert.DoesNotContain("Panel.Agent", Assemblies.References(context));
		}
	}
}

internal static class Assemblies
{
	private static readonly Dictionary<string, Assembly> loaded = new(StringComparer.Ordinal)
	{
		["Panel.SharedKernel"] = typeof(SharedKernel.InstanceId).Assembly,
		["Panel.Contracts"] = typeof(Contracts.PanelJson).Assembly,
		["Panel.Access"] = typeof(Access.Domain.Role).Assembly,
		["Panel.Configuration"] = typeof(Configuration.Domain.ServerConfiguration).Assembly,
		["Panel.Content"] = typeof(Content.Domain.ArchiveSafety).Assembly,
		["Panel.Fleet"] = typeof(Fleet.Domain.RegisteredServer).Assembly,
		["Panel.Maintenance"] = typeof(Maintenance.Domain.MaintenanceJob).Assembly,
		["Panel.Catalogues"] = typeof(Catalogues.Domain.GameVersion).Assembly,
		["Panel.Observability"] = typeof(Observability.Domain.LogEvent).Assembly,
		["Panel.Runtime"] = typeof(Runtime.Domain.ReadinessPolicy).Assembly,
		["Panel.Provisioning"] = typeof(Provisioning.Domain.SetupJourney).Assembly,
		["Panel.Agent.Client"] = typeof(Agent.Client.AgentClient).Assembly,
		["Panel.Web"] = typeof(Web.PanelOptions).Assembly,
		["Panel.Cli"] = typeof(Cli.Program).Assembly,
	};

	public static Assembly Of(string name) => loaded[name];

	public static IReadOnlyCollection<string> References(string name) =>
	[
		.. Of(name).GetReferencedAssemblies()
			.Select(reference => reference.Name ?? string.Empty)
			.Where(reference => reference.Length > 0),
	];

	public static IEnumerable<Assembly> All => loaded.Values;
}
