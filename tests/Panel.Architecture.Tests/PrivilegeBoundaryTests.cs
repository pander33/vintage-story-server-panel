using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Panel.Architecture.Tests;

/// <summary>
/// Запуск посторонних процессов допустим только в привилегированной сборке, чтобы её
/// поверхность оставалась обозримой. Проверяется по графу типов.
/// </summary>
public sealed class PrivilegeBoundaryTests
{
	/// <summary>Привилегированная сторона; кроме неё процессы запускает только установщик.</summary>
	private const string privileged = "Panel.Agent";

	[Theory]
	[InlineData("Panel.SharedKernel")]
	[InlineData("Panel.Contracts")]
	[InlineData("Panel.Access")]
	[InlineData("Panel.Catalogues")]
	[InlineData("Panel.Configuration")]
	[InlineData("Panel.Content")]
	[InlineData("Panel.Fleet")]
	[InlineData("Panel.Maintenance")]
	[InlineData("Panel.Observability")]
	[InlineData("Panel.Provisioning")]
	[InlineData("Panel.Runtime")]
	[InlineData("Panel.Agent.Client")]
	[InlineData("Panel.Web")]
	public void Запуск_процессов_только_в_привилегированной_сборке(string assembly)
	{
		var used = TypeReferences(Assemblies.Of(assembly));

		Assert.DoesNotContain("System.Diagnostics.Process", used);
		Assert.DoesNotContain("System.Diagnostics.ProcessStartInfo", used);
	}

	[Fact]
	public void Привилегированная_сборка_процессы_запускает()
	{
		// Обратная проверка: если запуск процессов отсюда исчезнет, значит он переехал, и тест выше
		// ничего не защищает.
		var used = TypeReferences(typeof(Agent.Program).Assembly);

		Assert.Contains("System.Diagnostics.Process", used);
		Assert.Equal(privileged, typeof(Agent.Program).Assembly.GetName().Name);
	}

	/// <summary>
	/// Установщик — второе и последнее исключение: он работает от root, пишет юниты и вызывает
	/// systemctl, useradd, runuser. Как и демон, он работает вне процесса панели
	/// (<see cref="ReferenceGraphTests.Установщик_и_панель_не_ссылаются_друг_на_друга"/>).
	/// </summary>
	[Fact]
	public void Установщик_процессы_запускает()
	{
		var used = TypeReferences(typeof(Cli.Program).Assembly);

		Assert.Contains("System.Diagnostics.Process", used);
		Assert.Equal("Panel.Cli", typeof(Cli.Program).Assembly.GetName().Name);
	}

	/// <summary>
	/// Полные имена типов, на которые ссылается сборка, из её метаданных. Рефлексия по
	/// загруженным типам не подходит: вызов Process.Start() не виден ни в одной сигнатуре.
	/// </summary>
	private static HashSet<string> TypeReferences(Assembly assembly)
	{
		using var stream = File.OpenRead(assembly.Location);
		using var portable = new PEReader(stream);
		var metadata = portable.GetMetadataReader();

		var names = new HashSet<string>(StringComparer.Ordinal);
		foreach (var handle in metadata.TypeReferences)
		{
			var reference = metadata.GetTypeReference(handle);
			var name = metadata.GetString(reference.Name);
			var space = metadata.GetString(reference.Namespace);
			names.Add(space.Length == 0 ? name : $"{space}.{name}");
		}

		return names;
	}
}
