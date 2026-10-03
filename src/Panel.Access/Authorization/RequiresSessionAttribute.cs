namespace Panel.Access.Authorization;

/// <summary>
/// Команда выполняется только из сессии браузера, не по API-токену: учётные записи, второй
/// фактор и сами токены. Утёкший токен не должен выпускать новые токены, менять пароли или
/// выключать второй фактор.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RequiresSessionAttribute : Attribute;
