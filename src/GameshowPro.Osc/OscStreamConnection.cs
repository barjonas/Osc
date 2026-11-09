namespace GameshowPro.Osc;

/// <summary>Settings for an <see cref="OscStreamConnection"/>.</summary>
public sealed record OscStreamConnectionOptions
{
    public static OscStreamConnectionOptions Default { get; } = new();

    /// <summary>Settings for a Ventuz Remoting 4 connection: length-prefixed frames and the Remoting dialect both ways.</summary>
    public static OscStreamConnectionOptions VentuzRemoting { get; } = new()
    {
        Writer = OscWriterOptions.VentuzRemoting,
        Reader = new OscReaderOptions { ArrayEncoding = OscArrayEncoding.VentuzRemoting },
    };

    /// <summary>How packets are delimited. Defaults to <see cref="OscStreamFraming.LengthPrefixed"/>.</summary>
    public OscStreamFraming Framing { get; init; } = OscStreamFraming.LengthPrefixed;

    public OscWriterOptions Writer { get; init; } = OscWriterOptions.Default;

    public OscReaderOptions Reader { get; init; } = OscReaderOptions.Default;

    /// <summary>
    /// Largest packet accepted from the peer. A longer length prefix, or a SLIP frame still open past this many
    /// bytes, fails the connection with <see cref="OscFormatException"/> instead of buffering without bound.
    /// Defaults to 1 MiB.
    /// </summary>
    public int MaxPacketLength
    {
        get;
        init => field = value >= OscBinary.MinimumMessageLength ? value : throw new ArgumentOutOfRangeException(nameof(MaxPacketLength), value, "Must be at least 8.");
    } = 1 << 20;

    /// <summary>Leaves the underlying stream open when the connection is disposed.</summary>
    public bool LeaveOpen { get; init; }
}

/// <summary>
/// Sends and receives framed packets over any duplex <see cref="Stream"/>, typically a <c>NetworkStream</c> from a
/// <c>TcpClient</c> or <c>TcpListener</c>. Connecting, binding to an interface, TLS and reconnection stay with the
/// host; this type only frames, buffers and decodes.
/// </summary>
/// <remarks>
/// Sends are serialized, so several callers may send concurrently. Only one <see cref="Receive"/> call should be in
/// flight at a time. A malformed packet or corrupt framing throws <see cref="OscFormatException"/> from
/// <see cref="Receive"/>; the stream cannot be resynchronized after that, so the caller should close it.
/// </remarks>
public sealed class OscStreamConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly OscStreamConnectionOptions _options;
    private readonly PipeReader _reader;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ArrayBufferWriter<byte> _sendBuffer = new();
    private readonly ArrayBufferWriter<byte> _slipBuffer = new();
    private long _sent;
    private long _received;
    private bool _disposed;

    public OscStreamConnection(Stream stream, OscStreamConnectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _options = options ?? OscStreamConnectionOptions.Default;
        _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
    }

    public long SentCount => Interlocked.Read(ref _sent);
    public long ReceivedCount => Interlocked.Read(ref _received);

    public void Send(OscPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        _sendGate.Wait();
        try
        {
            _stream.Write(Frame(packet).Span);
            _stream.Flush();
            _ = Interlocked.Increment(ref _sent);
        }
        finally
        {
            _ = _sendGate.Release();
        }
    }

    public async ValueTask SendAsync(OscPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(Frame(packet), cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            _ = Interlocked.Increment(ref _sent);
        }
        finally
        {
            _ = _sendGate.Release();
        }
    }

    /// <summary>Waits for the next packet, or returns null when the peer closed the stream cleanly between frames.</summary>
    public async ValueTask<OscPacket?> Receive(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            ReadResult result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;
            if (TryReadPacket(ref buffer, out OscPacket? packet))
            {
                _reader.AdvanceTo(buffer.Start);
                _ = Interlocked.Increment(ref _received);
                return packet;
            }
            _reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
            {
                return buffer.Length == 0 || IsOnlySlipEnds(buffer) ? null : throw new OscFormatException(OscParseError.UnexpectedEnd);
            }
            if (result.IsCanceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    /// <summary>Yields packets until the peer closes the stream or <paramref name="cancellationToken"/> is cancelled.</summary>
    public async IAsyncEnumerable<OscPacket> ReceiveAll([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await Receive(cancellationToken).ConfigureAwait(false) is { } packet)
        {
            yield return packet;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _reader.CompleteAsync().ConfigureAwait(false);
        if (!_options.LeaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        _sendGate.Dispose();
    }

    private ReadOnlyMemory<byte> Frame(OscPacket packet)
    {
        _sendBuffer.ResetWrittenCount();
        _ = OscStreamFramer.WriteFrame(_sendBuffer, packet, _options.Framing, _options.Writer);
        return _sendBuffer.WrittenMemory;
    }

    private bool TryReadPacket(ref ReadOnlySequence<byte> buffer, [NotNullWhen(true)] out OscPacket? packet)
    {
        switch (_options.Framing)
        {
            case OscStreamFraming.LengthPrefixed:
                {
                    if (!OscStreamFramer.TryReadLengthPrefixedFrame(buffer, out ReadOnlySequence<byte> frame, out SequencePosition consumed, _options.MaxPacketLength))
                    {
                        packet = null;
                        return false;
                    }
                    packet = OscPacket.Parse(frame, _options.Reader);
                    buffer = buffer.Slice(consumed);
                    return true;
                }
            case OscStreamFraming.Slip:
                {
                    _slipBuffer.ResetWrittenCount();
                    if (!OscStreamFramer.TryReadSlipFrame(buffer, _slipBuffer, out int length, out SequencePosition consumed))
                    {
                        if (buffer.Length > OscStreamFramer.GetMaxSlipFrameLength(_options.MaxPacketLength))
                        {
                            throw new OscFormatException(OscParseError.InvalidElementSize);
                        }
                        packet = null;
                        return false;
                    }
                    packet = OscPacket.Parse(_slipBuffer.WrittenSpan[..length], _options.Reader);
                    buffer = buffer.Slice(consumed);
                    return true;
                }
            default:
                throw new UnreachableException();
        }
    }

    private static bool IsOnlySlipEnds(in ReadOnlySequence<byte> buffer)
    {
        SequenceReader<byte> reader = new(buffer);
        _ = reader.AdvancePast(0xC0);
        return reader.End;
    }
}
