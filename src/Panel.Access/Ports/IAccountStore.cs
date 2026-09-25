using Panel.Access.Domain;
using Panel.SharedKernel;

namespace Panel.Access.Ports;

/// <summary>
/// Хранилище учётных записей. Читает и пишет справочник целиком, потому что правила
/// относятся к набору записей.
/// </summary>
public interface IAccountStore
{
	Task<Result<AccountDirectory>> LoadAsync(CancellationToken ct);

	Task<Result<Done>> SaveAsync(AccountDirectory directory, CancellationToken ct);
}
