using System.Net;

namespace Panel.Runtime.Ports;

/// <summary>Свободен ли игровой порт на этой машине.</summary>
public enum PortAvailability
{
	Free,

	/// <summary>Порт слушает другая программа.</summary>
	InUse,

	/// <summary>Система не даёт занять порт: на Windows — исключённый диапазон Hyper-V или WinNAT.</summary>
	Denied,

	/// <summary>Проверить не удалось (например, адрес привязки не принадлежит машине); запуск не запрещается.</summary>
	Unknown,
}

/// <summary>
/// Проверка порта перед запуском остановленного сервера: занятый порт иначе обнаружился бы
/// только по журналу упавшей игры.
/// </summary>
/// <remarks>
/// Панель и игра всегда на одной машине, поэтому проверяет сама панель, пробной привязкой, без
/// демона: занять порт выше 1024 может любой пользователь.
/// </remarks>
public interface IGamePortProbe
{
	/// <summary><paramref name="address"/> — адрес привязки из <c>serverconfig.json</c>; <c>null</c> — все адреса.</summary>
	PortAvailability Probe(IPAddress? address, int port);
}
