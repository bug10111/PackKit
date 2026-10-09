namespace PackKit.Core;

/// <summary>
/// 基础流只读切片视图：把基础流的 [offset, offset+length) 区间暴露为独立可 Seek 流，供 zip 归档按偏移打开。
/// </summary>
internal sealed class SubStream : Stream
{
    private readonly Stream baseStream;

    private readonly long baseOffset;

    private readonly long sliceLength;

    private long position;

    /// <summary>
    /// 创建切片视图（不接管基础流生命周期，不负责关闭基础流）。
    /// </summary>
    /// <param name="baseStream">基础只读流（必须可 Seek）。</param>
    /// <param name="baseOffset">切片起始偏移。</param>
    /// <param name="sliceLength">切片字节长度。</param>
    public SubStream(Stream baseStream, long baseOffset, long sliceLength)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        if (!baseStream.CanRead || !baseStream.CanSeek)
        {
            throw new ArgumentException("基础流必须可读且可 Seek。", nameof(baseStream));
        }

        if (baseOffset < 0 || sliceLength < 0 || baseOffset + sliceLength > baseStream.Length)
        {
            throw new ArgumentException($"切片区间非法（offset={baseOffset}, length={sliceLength}, 基础流长度={baseStream.Length}）。", nameof(sliceLength));
        }

        this.baseStream = baseStream;
        this.baseOffset = baseOffset;
        this.sliceLength = sliceLength;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => sliceLength;

    /// <inheritdoc />
    public override long Position
    {
        get => position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        long remaining = sliceLength - position;
        if (remaining <= 0)
        {
            return 0;
        }

        int requested = (int)Math.Min(buffer.Length, remaining);
        baseStream.Seek(baseOffset + position, SeekOrigin.Begin);
        int read = await baseStream.ReadAsync(buffer.Slice(0, requested), cancellationToken);
        position += read;
        return read;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        long remaining = sliceLength - position;
        if (remaining <= 0)
        {
            return 0;
        }

        int requested = (int)Math.Min(count, remaining);
        baseStream.Seek(baseOffset + position, SeekOrigin.Begin);
        int read = baseStream.Read(buffer, offset, requested);
        position += read;
        return read;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => sliceLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "未知的 SeekOrigin。")
        };

        if (target < 0 || target > sliceLength)
        {
            throw new IOException($"切片流定位越界（目标位置={target}, 切片长度={sliceLength}）。");
        }

        position = target;
        return position;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        throw new NotSupportedException("切片流为只读视图，不支持改写长度。");
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("切片流为只读视图，不支持写入。");
    }
}
