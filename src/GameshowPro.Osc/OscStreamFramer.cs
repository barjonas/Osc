namespace GameshowPro.Osc;

/// <summary>How packets are delimited on a stream transport such as TCP.</summary>
public enum OscStreamFraming
{
    /// <summary>Each packet is preceded by its length as a big-endian int32. Used by Ventuz Remoting and OSC 1.0 over TCP.</summary>
    LengthPrefixed,

    /// <summary>Each packet is wrapped in SLIP (RFC 1055) END bytes with ESC escaping, as OSC 1.1 recommends.</summary>
    Slip,
}

/// <summary>
/// Adds and removes stream framing. UDP needs none: each datagram is one packet. <see cref="OscStreamConnection"/>
/// drives these over a <see cref="Stream"/>; use them directly when you own the buffering.
/// </summary>
public static class OscStreamFramer
{
    private const byte SlipEnd = 0xC0;
    private const byte SlipEsc = 0xDB;
    private const byte SlipEscEnd = 0xDC;
    private const byte SlipEscEsc = 0xDD;

    /// <summary>Largest number of bytes a SLIP frame of a packet of <paramref name="packetLength"/> bytes can occupy.</summary>
    public static int GetMaxSlipFrameLength(int packetLength) => (2 * packetLength) + 2;

    /// <summary>Writes one framed packet and returns the number of bytes written, including the framing.</summary>
    public static int WriteFrame(IBufferWriter<byte> writer, OscPacket packet, OscStreamFraming framing, OscWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(packet);
        OscWriterOptions resolved = options ?? OscWriterOptions.Default;
        int count = packet.GetByteCount(resolved);
        switch (framing)
        {
            case OscStreamFraming.LengthPrefixed:
                {
                    Span<byte> span = writer.GetSpan(count + 4);
                    BinaryPrimitives.WriteInt32BigEndian(span, count);
                    OscWriter packetWriter = new(span.Slice(4, count));
                    packet.WriteCore(ref packetWriter, resolved);
                    writer.Advance(count + 4);
                    return count + 4;
                }
            case OscStreamFraming.Slip:
                {
                    byte[] rented = ArrayPool<byte>.Shared.Rent(count);
                    try
                    {
                        _ = packet.TryWrite(rented.AsSpan(0, count), out _, resolved);
                        Span<byte> span = writer.GetSpan(GetMaxSlipFrameLength(count));
                        int written = EncodeSlip(rented.AsSpan(0, count), span);
                        writer.Advance(written);
                        return written;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(framing), framing, "Unknown framing.");
        }
    }

    /// <summary>
    /// Extracts the first complete length-prefixed packet from <paramref name="buffer"/>. Returns false when more
    /// data is needed; throws <see cref="OscFormatException"/> when the prefix cannot be a valid packet length or
    /// exceeds <paramref name="maxPacketLength"/>.
    /// </summary>
    public static bool TryReadLengthPrefixedFrame(ReadOnlySpan<byte> buffer, out ReadOnlySpan<byte> packet, out int bytesConsumed, int maxPacketLength = int.MaxValue)
    {
        packet = default;
        bytesConsumed = 0;
        if (buffer.Length < 4)
        {
            return false;
        }
        int length = ValidatePrefix(BinaryPrimitives.ReadInt32BigEndian(buffer), maxPacketLength);
        if (buffer.Length - 4 < length)
        {
            return false;
        }
        packet = buffer.Slice(4, length);
        bytesConsumed = 4 + length;
        return true;
    }

    /// <summary>
    /// The <see cref="ReadOnlySequence{T}"/> form for <c>PipeReader</c> buffers: no copy is made, and
    /// <paramref name="consumed"/> is the position after the frame to pass to <c>AdvanceTo</c>.
    /// </summary>
    public static bool TryReadLengthPrefixedFrame(in ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> packet, out SequencePosition consumed, int maxPacketLength = int.MaxValue)
    {
        packet = default;
        consumed = buffer.Start;
        if (buffer.Length < 4)
        {
            return false;
        }
        Span<byte> prefix = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(prefix);
        int length = ValidatePrefix(BinaryPrimitives.ReadInt32BigEndian(prefix), maxPacketLength);
        if (buffer.Length - 4 < length)
        {
            return false;
        }
        packet = buffer.Slice(4, length);
        consumed = packet.End;
        return true;
    }

    /// <summary>
    /// Decodes the first complete SLIP frame from <paramref name="buffer"/> into <paramref name="destination"/>,
    /// which must be at least as long as the frame. Returns false when no END byte closes a frame yet; throws
    /// <see cref="OscFormatException"/> on an invalid escape sequence.
    /// </summary>
    public static bool TryReadSlipFrame(ReadOnlySpan<byte> buffer, Span<byte> destination, out int packetLength, out int bytesConsumed)
    {
        packetLength = 0;
        bytesConsumed = 0;
        int index = 0;
        while (index < buffer.Length && buffer[index] == SlipEnd)
        {
            index++;
        }
        int written = 0;
        while (index < buffer.Length)
        {
            byte b = buffer[index++];
            switch (b)
            {
                case SlipEnd:
                    packetLength = written;
                    bytesConsumed = index;
                    return true;
                case SlipEsc:
                    if (index >= buffer.Length)
                    {
                        return false;
                    }
                    b = Unescape(buffer[index++]);
                    break;
                default:
                    break;
            }
            destination[written++] = b;
        }
        return false;
    }

    /// <summary>
    /// The <see cref="ReadOnlySequence{T}"/> form for <c>PipeReader</c> buffers. The unescaped packet is written to
    /// <paramref name="destination"/>; <paramref name="consumed"/> is the position after the closing END byte.
    /// </summary>
    public static bool TryReadSlipFrame(in ReadOnlySequence<byte> buffer, IBufferWriter<byte> destination, out int packetLength, out SequencePosition consumed)
    {
        ArgumentNullException.ThrowIfNull(destination);
        packetLength = 0;
        consumed = buffer.Start;
        SequenceReader<byte> reader = new(buffer);
        _ = reader.AdvancePast(SlipEnd);
        int written = 0;
        while (reader.TryRead(out byte b))
        {
            switch (b)
            {
                case SlipEnd:
                    packetLength = written;
                    consumed = reader.Position;
                    return true;
                case SlipEsc:
                    if (!reader.TryRead(out byte escaped))
                    {
                        return false;
                    }
                    b = Unescape(escaped);
                    break;
                default:
                    break;
            }
            destination.GetSpan(1)[0] = b;
            destination.Advance(1);
            written++;
        }
        return false;
    }

    private static int ValidatePrefix(int length, int maxPacketLength)
        => length < OscBinary.MinimumMessageLength || length % 4 != 0 || length > maxPacketLength
            ? throw new OscFormatException(OscParseError.InvalidElementSize)
            : length;

    private static byte Unescape(byte escaped) => escaped switch
    {
        SlipEscEnd => SlipEnd,
        SlipEscEsc => SlipEsc,
        _ => throw new OscFormatException("Invalid SLIP escape sequence."),
    };

    private static int EncodeSlip(ReadOnlySpan<byte> packet, Span<byte> destination)
    {
        int written = 0;
        destination[written++] = SlipEnd;
        foreach (byte b in packet)
        {
            switch (b)
            {
                case SlipEnd:
                    destination[written++] = SlipEsc;
                    destination[written++] = SlipEscEnd;
                    break;
                case SlipEsc:
                    destination[written++] = SlipEsc;
                    destination[written++] = SlipEscEsc;
                    break;
                default:
                    destination[written++] = b;
                    break;
            }
        }
        destination[written++] = SlipEnd;
        return written;
    }
}
