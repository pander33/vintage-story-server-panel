using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Panel.Catalogues.Domain;
using Panel.SharedKernel;

namespace Panel.Catalogues.Infrastructure;

/// <summary>Релиз ModDB: оценка домена и поля для интерфейса.</summary>
public sealed record ModDbRelease(ModRelease Release, JsonObject Json);

/// <summary>Страница мода: ответ <c>/api/moddb/mod/{identifier}</c> и релизы для выбора.</summary>
/// <param name="LookupIdentifier">
/// Идентификатор, под которым нашёлся мод. У мода с битыми метаданными это номер страницы
/// из запасного поиска, и дальнейшие запросы идут по нему.
/// </param>
public sealed record ModDetails(JsonObject Json, IReadOnlyList<ModDbRelease> Releases, string LookupIdentifier);

/// <summary>Откуда скачивать релиз; хост и схема адреса уже проверены.</summary>
public sealed record ModDownload(Uri Url, string FileName);

/// <summary>Релиз для обновления (с полем <c>identifier</c>) или причина его отсутствия.</summary>
/// <param name="Prerelease">
/// Совместимый предрелиз новее стабильного выбора, если предрелизы не входили в выбор. Сам не
/// выбирается — только предлагается рядом.
/// </param>
public sealed record ReleaseCandidate(JsonObject? Release, ReleaseLookup Lookup, JsonObject? Prerelease = null);

/// <summary>
/// Клиент ModDB: перевод внешнего API в понятия панели.
/// </summary>
/// <remarks>
/// <para>
/// Любой отказ здесь — <see cref="FailureCode.Unavailable"/>, и маршрут отвечает 502: неверный
/// идентификатор и недоступная сеть — одна ошибка внешнего сервиса.
/// </para>
/// <para>
/// Правила, отмеченные на месте: запасной поиск по имени возвращает исходную ошибку,
/// совместимость проверяется заново при скачивании, хост загрузки один, размер ограничен и
/// у ответа API, и у архива. Массового обновления без подтверждения нет (INV-28): каждый
/// релиз подтверждается отдельно.
/// </para>
/// </remarks>
public sealed partial class ModDbClient
{
	public const string ApiBase = "https://mods.vintagestory.at/api";

	public const string DownloadHost = "moddbcdn.vintagestory.at";

	private const int maxResponseBytes = 4 * 1024 * 1024;

	/// <summary>Весь каталог одним ответом — около 3,8 МБ и растёт, поэтому свой предел.</summary>
	private const int maxCatalogueBytes = 16 * 1024 * 1024;

	/// <summary>Предел описания мода, которое уходит в браузер.</summary>
	private const int maxDescriptionChars = 200_000;

	private const string userAgent = "VintageStoryPanel/0.2.0";

	private static readonly string[] searchFields =
		["modid", "name", "summary", "author", "urlalias", "side", "logo", "lastreleased", "modidstrs"];

	private readonly HttpClient http;

	public ModDbClient(HttpClient http)
	{
		ArgumentNullException.ThrowIfNull(http);
		this.http = http;
	}

	[GeneratedRegex(@"\A[A-Za-z0-9._-]{1,128}\z", RegexOptions.CultureInvariant)]
	private static partial Regex SafeId { get; }

	[GeneratedRegex("[^a-z0-9]", RegexOptions.CultureInvariant)]
	private static partial Regex NotAlphanumeric { get; }

	/// <summary>Поиск: не больше 30 результатов, только поля для списка.</summary>
	public async Task<Result<JsonArray>> SearchAsync(string text, CancellationToken ct)
	{
		text = (text ?? string.Empty).Trim();
		var length = text.EnumerateRunes().Count();
		if (length is < 2 or > 80)
		{
			return Refused(Phrase.From("Введите от 2 до 80 символов"));
		}

		var value = await GetAsync(
			"/mods",
			$"?text={Uri.EscapeDataString(text)}&orderby=lastreleased&orderdirection=desc",
			ct).ConfigureAwait(false);
		if (!value.IsSuccess)
		{
			return Result.Fail<JsonArray>(value.Failure!);
		}

		var result = new JsonArray();
		var mods = value.Value["mods"] as JsonArray ?? [];
		foreach (var item in mods.Take(30).OfType<JsonObject>())
		{
			var summary = new JsonObject();
			foreach (var field in searchFields)
			{
				summary[field] = item[field]?.DeepClone();
			}

			result.Add(summary);
		}

		return result;
	}

	/// <summary>
	/// Весь каталог модов одним запросом. Записи, которые не моды, отбрасываются.
	/// </summary>
	public async Task<Result<IReadOnlyList<CatalogueEntry>>> ListCatalogueAsync(CancellationToken ct)
	{
		var value = await GetAsync("/mods", string.Empty, ct, maxCatalogueBytes, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
		if (!value.IsSuccess)
		{
			return Result.Fail<IReadOnlyList<CatalogueEntry>>(value.Failure!);
		}

		IReadOnlyList<CatalogueEntry> entries =
		[
			.. (value.Value["mods"] as JsonArray ?? [])
				.OfType<JsonObject>()
				.Select(CatalogueEntry.From)
				.OfType<CatalogueEntry>(),
		];
		return Result.Ok(entries);
	}

	/// <summary>Теги ModDB с цветами для фильтра.</summary>
	public async Task<Result<IReadOnlyList<CatalogueTag>>> ListTagsAsync(CancellationToken ct)
	{
		var value = await GetAsync("/tags", string.Empty, ct).ConfigureAwait(false);
		if (!value.IsSuccess)
		{
			return Result.Fail<IReadOnlyList<CatalogueTag>>(value.Failure!);
		}

		IReadOnlyList<CatalogueTag> tags =
		[
			.. (value.Value["tags"] as JsonArray ?? [])
				.OfType<JsonObject>()
				.Select(tag => new CatalogueTag(PythonText.Of(tag["name"]), PythonText.Of(tag["color"])))
				.Where(tag => tag.Name.Length > 0),
		];
		return Result.Ok(tags);
	}

	/// <summary>
	/// Номера модов, у которых есть релиз, отмеченный ровно этой версией игры (<c>gameversions[]</c>).
	/// Это подсказка для каталога: решает проверка релиза при показе и при скачивании. Версии игры
	/// нет в списке ModDB — пустое множество.
	/// </summary>
	public async Task<Result<IReadOnlySet<long>>> ListCompatibleAsync(GameVersion game, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(game);
		var versions = await GetAsync("/gameversions", string.Empty, ct).ConfigureAwait(false);
		if (!versions.IsSuccess)
		{
			return Result.Fail<IReadOnlySet<long>>(versions.Failure!);
		}

		var tag = (versions.Value["gameversions"] as JsonArray ?? [])
			.OfType<JsonObject>()
			.FirstOrDefault(item => PythonText.Of(item["name"]) == game.Text);
		if (IntegerOf(tag?["tagid"]) is not { } tagId)
		{
			return Result.Ok<IReadOnlySet<long>>(new HashSet<long>());
		}

		var mods = await GetAsync(
			"/mods",
			$"?gameversions%5B%5D={tagId.ToString(CultureInfo.InvariantCulture)}",
			ct,
			maxCatalogueBytes,
			TimeSpan.FromSeconds(45)).ConfigureAwait(false);
		if (!mods.IsSuccess)
		{
			return Result.Fail<IReadOnlySet<long>>(mods.Failure!);
		}

		IReadOnlySet<long> ids = (mods.Value["mods"] as JsonArray ?? [])
			.OfType<JsonObject>()
			.Select(item => IntegerOf(item["modid"]))
			.OfType<long>()
			.ToHashSet();
		return Result.Ok(ids);
	}

	/// <summary>
	/// Адрес картинки для браузера: через прокси панели, а не с CDN (CSP разрешает только свой хост).
	/// Чужой хост и не-https дают <c>null</c>.
	/// </summary>
	public static string? ProxiedImage(string? url) =>
		url is not null && TrustedDownload(url) is { } trusted
			? "/api/moddb/image?url=" + Uri.EscapeDataString(trusted.AbsoluteUri)
			: null;

	/// <summary>Страница мода и совместимость каждого релиза с версией игры.</summary>
	public async Task<Result<ModDetails>> DetailsAsync(
		string identifier,
		GameVersion game,
		string fallbackName,
		CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(game);
		if (!SafeId.IsMatch(identifier ?? string.Empty))
		{
			return Refused(Phrase.From("Некорректный идентификатор мода"));
		}

		var lookup = identifier!;
		var value = await GetAsync("/mod/" + Uri.EscapeDataString(identifier!), string.Empty, ct).ConfigureAwait(false);
		if (!value.IsSuccess)
		{
			// Мод с битыми метаданными не открывается по modid, но находится по имени. Если не
			// нашёлся и так, пользователь видит исходную ошибку. Отказ самого поиска возвращается как
			// есть.
			var searchText = (fallbackName ?? string.Empty).Trim() is { Length: > 0 } name ? name : identifier!;
			var normalized = Normalize(searchText);
			var candidates = await SearchAsync(searchText, ct).ConfigureAwait(false);
			if (!candidates.IsSuccess)
			{
				return Result.Fail<ModDetails>(candidates.Failure!);
			}

			var match = candidates.Value
				.OfType<JsonObject>()
				.FirstOrDefault(item => Normalize(PythonText.Of(item["name"])) == normalized);
			if (match is null || !PythonText.Truthy(match["modid"]))
			{
				return Result.Fail<ModDetails>(value.Failure!);
			}

			lookup = PythonText.Of(match["modid"]);
			value = await GetAsync("/mod/" + lookup, string.Empty, ct).ConfigureAwait(false);
			if (!value.IsSuccess)
			{
				return Result.Fail<ModDetails>(value.Failure!);
			}
		}

		if (value.Value["mod"] is not JsonObject mod)
		{
			return Refused(Phrase.From("Мод не найден"));
		}

		var releases = new List<ModDbRelease>();
		foreach (var release in (mod["releases"] as JsonArray ?? []).OfType<JsonObject>())
		{
			releases.Add(Describe(release, game));
		}

		var json = new JsonObject
		{
			["modid"] = mod["modid"]?.DeepClone(),
			["name"] = mod["name"]?.DeepClone(),
			["author"] = mod["author"]?.DeepClone(),
			["urlalias"] = mod["urlalias"]?.DeepClone(),
			["side"] = mod["side"]?.DeepClone(),
			["homepageurl"] = mod["homepageurl"]?.DeepClone(),
			["sourcecodeurl"] = mod["sourcecodeurl"]?.DeepClone(),
			["issuetrackerurl"] = mod["issuetrackerurl"]?.DeepClone(),
			["wikiurl"] = mod["wikiurl"]?.DeepClone(),
			["trailervideourl"] = mod["trailervideourl"]?.DeepClone(),
			["downloads"] = mod["downloads"]?.DeepClone(),
			["follows"] = mod["follows"]?.DeepClone(),
			["tags"] = mod["tags"] is JsonArray modTags ? modTags.DeepClone() : new JsonArray(),
			["created"] = mod["created"]?.DeepClone(),
			["lastreleased"] = mod["lastreleased"]?.DeepClone(),
			["text"] = Truncate(PythonText.Of(mod["text"]), maxDescriptionChars),
			["logo"] = ProxiedImage(PythonText.Of(mod["logofile"])),
			["screenshots"] = new JsonArray([.. Screenshots(mod)]),
			["releases"] = new JsonArray([.. releases.Select(release => (JsonNode) release.Json.DeepClone())]),
			["game_version"] = game.Text,
			["lookup_identifier"] = lookup,
		};

		return new ModDetails(json, releases, lookup);
	}

	/// <summary>
	/// Релиз для обновления или причина его отсутствия. У найденного релиза есть
	/// <c>identifier</c>, по нему и скачивать.
	/// </summary>
	public async Task<Result<ReleaseCandidate>> CompatibleReleaseAsync(
		string identifier,
		GameVersion game,
		bool includePrerelease,
		string fallbackName,
		CancellationToken ct)
	{
		var details = await DetailsAsync(identifier, game, fallbackName, ct).ConfigureAwait(false);
		if (!details.IsSuccess)
		{
			return Result.Fail<ReleaseCandidate>(details.Failure!);
		}

		IReadOnlyList<ModRelease> releases = [.. details.Value.Releases.Select(item => item.Release)];
		var lookup = ReleaseLookup.Choose(releases, includePrerelease);
		var prerelease = includePrerelease ? null : ReleaseLookup.NewerPrerelease(releases, lookup);
		var newer = prerelease is null ? null : Candidate(details.Value, prerelease);
		return lookup is ReleaseLookup.Found found
			? new ReleaseCandidate(Candidate(details.Value, found.Release), lookup, newer)
			: new ReleaseCandidate(null, lookup, newer);
	}

	/// <summary>Релиз для ответа: его поля и <c>identifier</c>, по которому скачивать.</summary>
	private static JsonObject Candidate(ModDetails details, ModRelease chosen)
	{
		var release = (JsonObject) details.Releases.First(item => ReferenceEquals(item.Release, chosen)).Json.DeepClone();
		release["identifier"] = details.LookupIdentifier;
		return release;
	}

	/// <summary>
	/// Адрес скачивания релиза. Совместимость проверяется заново по свежему ответу, чтобы не
	/// скачать релиз, который перестал быть совместимым после показа.
	/// </summary>
	public async Task<Result<ModDownload>> ResolveDownloadAsync(
		string identifier,
		long releaseId,
		GameVersion game,
		CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(game);
		if (!SafeId.IsMatch(identifier ?? string.Empty))
		{
			return Refused(Phrase.From("Некорректный идентификатор мода"));
		}

		var value = await GetAsync("/mod/" + Uri.EscapeDataString(identifier!), string.Empty, ct).ConfigureAwait(false);
		if (!value.IsSuccess)
		{
			return Result.Fail<ModDownload>(value.Failure!);
		}

		var mod = value.Value["mod"] as JsonObject ?? [];
		foreach (var release in (mod["releases"] as JsonArray ?? []).OfType<JsonObject>())
		{
			if (IntegerOf(release["releaseid"]) != releaseId)
			{
				continue;
			}

			if (!CompatibilityPolicy.Evaluate(TagsOf(release), game).IsCompatible)
			{
				return Refused(Phrase.Format($"Релиз не отмечен совместимым с Vintage Story {game.Text}"));
			}

			var url = TrustedDownload(PythonText.Of(release["mainfile"]));
			if (url is null)
			{
				return Refused(Phrase.From("ModDB вернул недопустимый адрес загрузки"));
			}

			var fileName = PythonText.Of(release["filename"]) is { Length: > 0 } name ? name : "mod.zip";
			return new ModDownload(url, fileName);
		}

		return Refused(Phrase.From("Релиз не найден"));
	}

	/// <summary>
	/// Скачать архив. Хост проверяется ещё раз, потому что адрес может прийти не только из
	/// <see cref="ResolveDownloadAsync"/>. Предел размера проверяется по заголовку и при чтении.
	/// </summary>
	public async Task<Result<byte[]>> DownloadAsync(Uri url, long maximum, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(url);
		if (TrustedDownload(url.OriginalString) is null)
		{
			return Refused(Phrase.From("Недопустимый адрес загрузки"));
		}

		var limit = TimeSpan.FromSeconds(30);
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(limit);
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			request.Headers.UserAgent.ParseAdd(userAgent);
			using var response = await http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
				.ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return Refused(Phrase.Format($"Не удалось скачать мод: {HttpError(response)}"));
			}

			if (response.Content.Headers.ContentLength > maximum)
			{
				return Refused(Phrase.From("Архив мода превышает лимит панели"));
			}

			await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
			var data = await ReadLimitedAsync(stream, maximum, timeout.Token).ConfigureAwait(false);
			return data is null ? Refused(Phrase.From("Архив мода превышает лимит панели")) : data;
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException
											  || (exception is OperationCanceledException && !ct.IsCancellationRequested))
		{
			return Refused(Phrase.Format($"Не удалось скачать мод: {Reachability.Explain(exception, url, limit)}"));
		}
	}

	/// <summary>
	/// Текст из HTML: три тега заменяются переносами, остальные теги вырезаются, четыре сущности
	/// разворачиваются. Результат показывается как текст, а не HTML; CSP защищает дополнительно.
	/// </summary>
	public static string StripHtml(string value)
	{
		value = LineBreakTags().Replace(value ?? string.Empty, "\n");
		value = AnyTag().Replace(value, string.Empty);
		return value
			.Replace("&quot;", "\"", StringComparison.Ordinal)
			.Replace("&amp;", "&", StringComparison.Ordinal)
			.Replace("&lt;", "<", StringComparison.Ordinal)
			.Replace("&gt;", ">", StringComparison.Ordinal)
			.Trim();
	}

	[GeneratedRegex(@"<br\s*/?>|</p>|</li>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex LineBreakTags();

	[GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
	private static partial Regex AnyTag();

	private static ModDbRelease Describe(JsonObject release, GameVersion game)
	{
		var tags = TagsOf(release);
		var domain = ModRelease.From(IntegerOf(release["releaseid"]), PythonText.Of(release["modversion"]), tags, game);
		var json = new JsonObject
		{
			["releaseid"] = release["releaseid"]?.DeepClone(),
			["version"] = release["modversion"]?.DeepClone(),
			["filename"] = release["filename"]?.DeepClone(),
			["created"] = release["created"]?.DeepClone(),
			["tags"] = release["tags"] is JsonArray raw ? raw.DeepClone() : new JsonArray(),
			["compatible"] = domain.Compatibility.IsCompatible,
			["compatibility"] = domain.Compatibility.Wire,
			["compatible_tag"] = domain.Compatibility.MatchedTag,
			["prerelease"] = domain.IsPrerelease,
			["changelog"] = Truncate(StripHtml(PythonText.Of(release["changelog"])), 2000),
		};

		return new ModDbRelease(domain, json);
	}

	/// <summary>Скриншоты мода: крупный и уменьшенный адрес, оба через прокси; чужие хосты пропускаются.</summary>
	private static IEnumerable<JsonNode> Screenshots(JsonObject mod)
	{
		foreach (var shot in (mod["screenshots"] as JsonArray ?? []).OfType<JsonObject>().Take(20))
		{
			var full = ProxiedImage(PythonText.Of(shot["mainfile"]));
			if (full is null)
			{
				continue;
			}

			yield return new JsonObject
			{
				["url"] = full,
				["thumbnail"] = ProxiedImage(PythonText.Of(shot["thumbnailfilename"])) ?? full,
			};
		}
	}

	/// <summary><c>str(tag)</c> каждого тега; если это не список, тегов нет.</summary>
	private static IReadOnlyList<string> TagsOf(JsonObject release) =>
		release["tags"] is JsonArray tags ? [.. tags.Select(StrOf)] : [];

	/// <summary><c>str(value)</c> без <c>or ""</c>: <c>null</c> даёт «None», ноль — «0».</summary>
	private static string StrOf(JsonNode? node) => node switch
	{
		null => "None",
		JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? "True" : "False",
		JsonValue value when value.TryGetValue<long>(out var integer) => integer.ToString(CultureInfo.InvariantCulture),
		_ => PythonText.Of(node) is { Length: > 0 } text ? text : node.ToJsonString().Trim('"'),
	};

	/// <summary>releaseid как целое: <c>7</c> и <c>7.0</c> подходят, строка <c>"7"</c> нет.</summary>
	private static long? IntegerOf(JsonNode? node)
	{
		if (node is not JsonValue value || value.TryGetValue<bool>(out _) || value.TryGetValue<string>(out _))
		{
			return null;
		}

		if (value.TryGetValue<long>(out var integer))
		{
			return integer;
		}

		return value.TryGetValue<double>(out var real) && real == Math.Floor(real) && Math.Abs(real) < long.MaxValue
			? (long) real
			: null;
	}

	internal static Uri? TrustedDownload(string candidate) =>
		Uri.TryCreate(candidate, UriKind.Absolute, out var url)
		&& url.Scheme == Uri.UriSchemeHttps
		&& url.Host.Equals(DownloadHost, StringComparison.OrdinalIgnoreCase)
			? url
			: null;

	private static string Normalize(string text) => NotAlphanumeric.Replace(text.ToLowerInvariant(), string.Empty);

	/// <summary><c>value[:2000]</c> по символам Unicode, не разрезая суррогатную пару.</summary>
	private static string Truncate(string value, int limit)
	{
		var runes = value.EnumerateRunes().Take(limit + 1).ToList();
		return runes.Count <= limit ? value : string.Concat(runes.Take(limit).Select(rune => rune.ToString()));
	}

	/// <summary><c>_json</c>: GET к API, не больше 4 МБ, объект со statuscode 200.</summary>
	private async Task<Result<JsonObject>> GetAsync(
		string path, string query, CancellationToken ct, int maximum = maxResponseBytes, TimeSpan? limit = null)
	{
		var wait = limit ?? TimeSpan.FromSeconds(15);
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(wait);

		byte[]? data;
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path + query);
			request.Headers.UserAgent.ParseAdd(userAgent);
			using var response = await http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
				.ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return Refused(Phrase.Format($"ModDB недоступен: {HttpError(response)}"));
			}

			await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
			data = await ReadLimitedAsync(stream, maximum, timeout.Token).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException
											  || (exception is OperationCanceledException && !ct.IsCancellationRequested))
		{
			return Refused(Phrase.Format($"ModDB недоступен: {Reachability.Explain(exception, new Uri(ApiBase), wait)}"));
		}

		if (data is null)
		{
			return Refused(Phrase.From("Ответ ModDB слишком большой"));
		}

		JsonNode? parsed;
		try
		{
			parsed = JsonNode.Parse(data);
		}
		catch (JsonException)
		{
			return Refused(Phrase.From("ModDB вернул некорректный JSON"));
		}

		if (parsed is not JsonObject value
			|| (value.ContainsKey("statuscode") && PythonTextOrNone(value["statuscode"]) != "200"))
		{
			return Refused(Phrase.From("ModDB отклонил запрос"));
		}

		return value;
	}

	/// <summary><c>str(value)</c> для statuscode: <c>null</c> даёт «None», а не пустую строку.</summary>
	private static string PythonTextOrNone(JsonNode? node) => node is null ? "None" : StrOf(node);

	/// <summary>Текст ошибки HTTP для сообщения об отказе.</summary>
	private static string HttpError(HttpResponseMessage response) =>
		string.Create(
			CultureInfo.InvariantCulture,
			$"HTTP Error {(int) response.StatusCode}: {response.ReasonPhrase ?? ((HttpStatusCode) response.StatusCode).ToString()}");

	/// <summary>Не больше <paramref name="maximum"/> байт; если больше, <c>null</c>.</summary>
	private static async Task<byte[]?> ReadLimitedAsync(Stream stream, long maximum, CancellationToken ct)
	{
		using var buffer = new MemoryStream();
		var chunk = new byte[81920];
		int read;
		while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
		{
			if (buffer.Length + read > maximum)
			{
				return null;
			}

			buffer.Write(chunk, 0, read);
		}

		return buffer.ToArray();
	}

	private static Failure Refused(Phrase message) => Failure.Unavailable(message);
}
