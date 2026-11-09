namespace GameshowPro.Osc;

/// <summary>
/// Forward-only encoder over a caller-supplied buffer. Every write is big-endian and padded as OSC requires.
/// The buffer must already be large enough: size it with <see cref="OscPacket.GetByteCount"/>, or for
/// hand-built messages with <see cref="OscBinary"/> arithmetic, because a write past the end throws.
/// </summary>
/// <remarks>
/// Most callers use <see cref="OscPacket.TryWrite"/> or <see cref="OscPacket.Write"/>. This type is for
/// streaming a message whose arguments are known in advance without building an <see cref="OscMessage"/>:
/// write the address, then the type tags, then each argument in order.
/// </remarks>
public ref struct OscWriter(Span<byte> buffer)
{
    private readonly Span<byte> _buffer = buffer;
    private int _position;

    public readonly int BytesWritten => _position;
    public readonly int Remaining => _buffer.Length - _position;

    /// <summary>The bytes written so far.</summary>
    public readonly Span<byte> Written => _buffer[.._position];

    public void WriteByte(byte value) => _buffer[_position++] = value;

    public void WriteInt32(int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(_buffer.Slice(_position, 4), value);
        _position += 4;
    }

    public void WriteUInt32(uint value) => WriteInt32(unchecked((int)value));

    public void WriteInt64(long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(_buffer.Slice(_position, 8), value);
        _position += 8;
    }

    public void WriteUInt64(ulong value) => WriteInt64(unchecked((long)value));
    public void WriteSingle(float value) => WriteInt32(BitConverter.SingleToInt32Bits(value));
    public void WriteDouble(double value) => WriteInt64(BitConverter.DoubleToInt64Bits(value));
    public void WriteChar(char value) => WriteInt32(value);
    public void WriteTimeTag(OscTimeTag value) => WriteUInt64(value.Value);
    public void WriteColor(OscColor value) => WriteUInt32(value.ToPacked());
    public void WriteMidi(OscMidiMessage value) => WriteUInt32(value.ToPacked());

    /// <summary>Writes a string as UTF-8 with its terminator and zero padding to a four-byte boundary.</summary>
    public void WriteString(ReadOnlySpan<char> value)
    {
        _position += Encoding.UTF8.GetBytes(value, _buffer[_position..]);
        Terminate();
    }

    /// <summary>Writes an already encoded string with its terminator and zero padding.</summary>
    public void WriteUtf8String(ReadOnlySpan<byte> utf8)
    {
        utf8.CopyTo(_buffer[_position..]);
        _position += utf8.Length;
        Terminate();
    }

    /// <summary>Writes a blob: its length, the bytes, and zero padding to a four-byte boundary.</summary>
    public void WriteBlob(ReadOnlySpan<byte> blob)
    {
        WriteInt32(blob.Length);
        blob.CopyTo(_buffer[_position..]);
        _position += blob.Length;
        Pad();
    }

    /// <summary>Writes a validated address string.</summary>
    public void WriteAddress(string address) => WriteString(OscAddress.Validate(address));

    /// <summary>Writes a type tag string. Pass the tags without the leading comma, for example <c>"ifs"u8</c>.</summary>
    public void WriteTypeTags(ReadOnlySpan<byte> typeTags)
    {
        WriteByte((byte)',');
        WriteUtf8String(typeTags);
    }

    /// <summary>Writes <c>#bundle</c> and the time tag.</summary>
    public void WriteBundleHeader(OscTimeTag timeTag)
    {
        OscBinary.BundlePrefix.CopyTo(_buffer[_position..]);
        _position += OscBinary.BundlePrefix.Length;
        WriteTimeTag(timeTag);
    }

    /// <summary>Writes a whole packet at the current position.</summary>
    public void WritePacket(OscPacket packet, OscWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(packet);
        packet.WriteCore(ref this, options ?? OscWriterOptions.Default);
    }

    /// <summary>Skips <paramref name="count"/> bytes and returns them for the caller to fill in later.</summary>
    internal Span<byte> Reserve(int count)
    {
        Span<byte> reserved = _buffer.Slice(_position, count);
        _position += count;
        return reserved;
    }

    private void Terminate()
    {
        WriteByte(0);
        Pad();
    }

    private void Pad()
    {
        int aligned = OscBinary.Align(_position);
        _buffer[_position..aligned].Clear();
        _position = aligned;
    }
}
