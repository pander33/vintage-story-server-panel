using Panel.Catalogues.Domain;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>Правило версии игры при установке пакета сервера.</summary>
public static class TransferVersion
{
	/// <summary>
	/// Версия новее версии в пакете обновит мир без возврата, старее — мир не откроет. Неизвестная
	/// версия источника или неразбираемая запись ничего не запрещают: судить не по чему.
	/// </summary>
	public static Failure? Check(string packaged, string chosen, bool acceptUpgrade)
	{
		if (ModVersion.Parse(packaged) is not { } source || ModVersion.Parse(chosen) is not { } target)
		{
			return null;
		}

		if (target < source)
		{
			return Failure.Conflict($"Версия игры {chosen} старее версии сервера в пакете ({packaged}): мир не откроется");
		}

		return target > source && !acceptUpgrade
			? Failure.Conflict($"Версия игры {chosen} новее версии в пакете ({packaged}): мир будет обновлён без возврата. Подтвердите обновление")
			: null;
	}
}
