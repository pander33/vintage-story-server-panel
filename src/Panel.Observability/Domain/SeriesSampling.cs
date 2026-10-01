namespace Panel.Observability.Domain;

/// <summary>Равномерная выборка с сохранением краёв без создания всех выходных точек.</summary>
internal static class SeriesSampling
{
	internal const int Limit = 720;

	internal static IReadOnlyList<TResult> Select<TSource, TResult>(
		IEnumerable<TSource> source, int count, Func<TSource, TResult> project)
	{
		if (count == 0)
		{
			return [];
		}

		var length = Math.Min(count, Limit);
		var result = new TResult[length];
		var selected = 0;
		var index = 0;
		foreach (var item in source)
		{
			var target = length == 1 ? 0 : (int) ((long) selected * (count - 1) / (length - 1));
			if (index++ == target)
			{
				result[selected++] = project(item);
				if (selected == length)
				{
					break;
				}
			}
		}

		return result;
	}
}
