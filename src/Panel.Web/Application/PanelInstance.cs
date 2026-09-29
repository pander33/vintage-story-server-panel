using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Сервер панели по умолчанию (<c>PANEL_INSTANCE</c>), каким он стал с момента запуска.
/// </summary>
/// <remarks>
/// <see cref="PanelOptions.DefaultInstance"/> прочитан из окружения при старте. Удаление этого
/// сервера переписывает строку в <c>vs-panel.env</c> через демон, и панель должна пользоваться
/// новым значением сразу, а не после перезапуска: иначе запросы без реестра уходили бы к
/// удалённому серверу.
/// </remarks>
public sealed class PanelInstance
{
	private volatile string current;

	public PanelInstance(PanelOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		current = options.DefaultInstance;
	}

	/// <summary>Имя как есть: до мастера установки оно может и не быть допустимым идентификатором.</summary>
	public string Value => current;

	public InstanceId Id => InstanceId.Parse(current);

	public bool Is(InstanceId instance) => instance.Value == current;

	internal void MoveTo(InstanceId instance) => current = instance.Value;
}
