using System.Collections;

namespace Panel.Observability.Domain;

/// <summary>История с дешёвым удалением начала и освобождением истёкших ссылок.</summary>
internal sealed class RetainedHistory<T> : IReadOnlyList<T> where T : class
{
	private readonly List<T?> items = [];
	private int head;

	public int Count => items.Count - head;

	public T this[int index] => index >= 0 && index < Count
		? items[head + index]!
		: throw new ArgumentOutOfRangeException(nameof(index));

	internal void Add(T item) => items.Add(item);

	internal void DiscardBefore(Predicate<T> retain)
	{
		while (head < items.Count && !retain(items[head]!))
		{
			items[head++] = null;
		}

		if (head >= 1024 && head >= items.Count / 2)
		{
			items.RemoveRange(0, head);
			head = 0;
		}
	}

	public IEnumerator<T> GetEnumerator()
	{
		for (var index = head; index < items.Count; index++)
		{
			yield return items[index]!;
		}
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
