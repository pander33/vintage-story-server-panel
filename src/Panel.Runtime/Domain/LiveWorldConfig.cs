using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Runtime.Domain;

/// <summary>
/// Живой <c>World.Config</c> работающего мира: проекция телеметрии, только для чтения.
/// </summary>
/// <remarks>
/// Парный тип к <c>WorldTemplate</c> контекста Configuration (INV-20), находится в другой
/// сборке, и преобразования между ними нет. Изменить живую конфигурацию можно только
/// командой <c>WorldConfigCommand</c> через порт <c>ILiveWorldConfig</c>; изменяющих методов у
/// этого типа нет.
/// </remarks>
public sealed record LiveWorldConfig(bool Available, JsonObject Configuration, Phrase? Detail);
