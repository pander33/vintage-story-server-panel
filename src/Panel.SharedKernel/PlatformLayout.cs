namespace Panel.SharedKernel;

/// <summary>
/// Платформа, которой принадлежат пути и транспорт этой установки. Берётся из
/// <c>/etc/vs-panel.json</c> (<c>layout</c>) и определяет правило абсолютности пути: одно
/// правило либо отвергло бы <c>C:\ProgramData</c>, либо приняло бы бессмыслицу.
/// </summary>
public enum PlatformLayout
{
	Linux,
	Windows,
}
