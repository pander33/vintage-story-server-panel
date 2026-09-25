using System.Reflection;
using Panel.Access.Authorization;

namespace Panel.Architecture.Tests;

/// <summary>
/// Форма доменных сигнатур и обязательность объявленного права.
/// </summary>
public sealed class DomainSignatureTests
{
	/// <summary>
	/// В доменных методах нет голого string.
	/// </summary>
	/// <remarks>
	/// Правило ADR-01: строка на входе домена означает, что проверка идентификатора, пути или
	/// имени юнита либо уже была, либо её не будет. Исключения перечислены явно:
	/// — фабрики (Parse/Create/FromWire), которые превращают строку в тип;
	/// — тексты для интерфейса (Detail, Reason, Message);
	/// — методы, унаследованные от object.
	/// </remarks>
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
	public void В_доменных_методах_нет_голого_string(string assemblyName)
	{
		var offenders = new List<string>();
		var examined = 0;

		foreach (var type in Assemblies.Of(assemblyName).GetTypes())
		{
			if (!(type.Namespace?.Contains(".Domain", StringComparison.Ordinal) ?? false) || !type.IsPublic)
			{
				continue;
			}

			foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
			{
				if (IsExempt(type, method))
				{
					continue;
				}

				examined++;
				if (method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string)))
				{
					offenders.Add($"{type.Name}.{method.Name}");
				}
			}
		}

		// Без счётчика переименование пространства имён сделало бы тест зелёным, но бесполезным.
		Assert.True(examined > 0, $"В {assemblyName} не найдено ни одного доменного метода для проверки");
		Assert.True(offenders.Count == 0, "Голый string в доменных методах: " + string.Join(", ", offenders));
	}

	/// <summary>
	/// Каждая команда объявляет право; без атрибута тест падает.
	/// </summary>
	[Fact]
	public void Каждая_команда_объявляет_требуемое_право()
	{
		var commands = Assemblies.All
			.SelectMany(assembly => assembly.GetTypes())
			.Where(type => typeof(ICommand).IsAssignableFrom(type) && type is { IsInterface: false, IsAbstract: false })
			.ToArray();

		Assert.NotEmpty(commands);

		var undeclared = commands
			.Where(type => type.GetCustomAttribute<RequiresAttribute>() is null)
			.Select(type => type.FullName ?? type.Name)
			.ToArray();

		Assert.True(undeclared.Length == 0, "Команды без [Requires]: " + string.Join(", ", undeclared));
	}

	private static bool IsExempt(Type type, MethodInfo method)
	{
		if (method.IsSpecialName)
		{
			// Свойства и операторы описывают данные, а не поведение.
			return true;
		}

		if (method.DeclaringType == typeof(object) || method.Name is "ToString" or "Equals" or "GetHashCode")
		{
			return true;
		}

		// Фабрика — статический метод, имя которого начинается с Parse, Create или From. Правило по
		// префиксу, а не по списку имён: список пришлось бы пополнять для каждой новой фабрики.
		// Только через фабрику строка может попасть в домен.
		if (method.IsStatic && (method.Name.StartsWith("Parse", StringComparison.Ordinal)
								|| method.Name.StartsWith("Create", StringComparison.Ordinal)
								|| method.Name.StartsWith("From", StringComparison.Ordinal)))
		{
			return true;
		}

		// Преобразование значения для протокола: строка здесь и есть результат.
		return method.IsStatic && method.Name is "ToWire" && method.GetParameters().Length == 1
			&& method.GetParameters()[0].ParameterType == type;
	}
}
