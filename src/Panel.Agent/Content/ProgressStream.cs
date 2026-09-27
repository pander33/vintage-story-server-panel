namespace Panel.Agent.Content;

/// <summary>Читаемый поток, который сообщает области операции, сколько байт из него прочитано.</summary>
public sealed class ProgressStream : Stream
{
	private readonly Stream inner;
	private readonly OperationScope? scope;

	public ProgressStream(Stream inner, OperationScope? scope)
	{
		ArgumentNullException.ThrowIfNull(inner);
		this.inner = inner;
		this.scope = scope;
	}

	public override bool CanRead => inner.CanRead;

	public override bool CanSeek => inner.CanSeek;

	public override bool CanWrite => false;

	public override long Length => inner.Length;

	public override long Position
	{
		get => inner.Position;
		set => inner.Position = value;
	}

	public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

	public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
		Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

	public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

	public override void Flush()
	{
	}

	public override void SetLength(long value) => throw new NotSupportedException();

	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	private int Count(int read)
	{
		if (read > 0)
		{
			scope?.Advance(read);
		}

		return read;
	}
}
