using Panel.Access.Authorization;
using Panel.Access.Domain;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Показать состояние сервера. Ничего не меняет, поэтому доступно и наблюдателю.
/// </summary>
[Requires(Permission.ObserveServer)]
public sealed record GetServerStatus(InstanceId Instance) : ICommand;
