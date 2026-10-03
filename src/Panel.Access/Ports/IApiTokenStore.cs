using Panel.Access.Domain;
using Panel.SharedKernel;

namespace Panel.Access.Ports;

/// <summary>
/// Хранилище API-токенов. Изменение — чтение, правило и запись под одним затвором: два
/// одновременных выпуска не должны потерять один из токенов.
/// </summary>
public interface IApiTokenStore
{
	Task<Result<ApiTokenSet>> LoadAsync(CancellationToken ct);

	Task<Result<T>> ChangeAsync<T>(Func<ApiTokenSet, Result<(ApiTokenSet Tokens, T Value)>> change, CancellationToken ct);
}
