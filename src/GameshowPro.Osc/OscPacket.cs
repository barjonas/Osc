namespace GameshowPro.Osc;

/// <summary>
/// A unit that travels in one datagram or stream frame: either an <see cref="OscMessage"/> or an
/// <see cref="OscBundle"/>. The hierarchy is closed. Every level encodes itself the same way, so a nested
/// bundle, a bundle and a bare message all serialize through the same members.
/// </summary>
public abstract class OscPacket
{
    private protected OscPacket()
    {
    }

    /// <summary>The exact number of bytes <see cref="TryWrite"/> produces for this packet.</summary>
    public abstract int GetByteCount(OscWriterOptions? options = null);

    /// <summary>
    /// The messages this packet carries in wire order: the message itself, or every message in a bundle and its
    /// nested bundles. Lets a receiver treat a bare message and a bundle alike.
    /// </summary>
    public abstract IEnumerable<OscMessage> EnumerateMessages();

    internal abstract void WriteCore(ref OscWriter writer, OscWriterOptions options);

    /// <summary>
    /// Writes the packet into <paramref name="destination"/> in one pass. Returns false, writing nothing, when
    /// the buffer is smaller than <see cref="GetByteCount"/>.
    /// </summary>
    public bool TryWrite(Span<byte> destination, out int bytesWritten, OscWriterOptions? options = null)
    {
        OscWriterOptions resolved = options ?? OscWriterOptions.Default;
        int count = GetByteCount(resolved);
        if (destination.Length < count)
        {
            bytesWritten = 0;
            return false;
        }
        OscWriter writer = new(destination[..count]);
        WriteCore(ref writer, resolved);
        bytesWritten = writer.BytesWritten;
        Debug.Assert(bytesWritten == count, "GetByteCount and WriteCore disagree.");
        return true;
    }

    /// <summary>Writes the packet to a buffer writer and returns the number of bytes written.</summary>
    public int Write(IBufferWriter<byte> writer, OscWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        OscWriterOptions resolved = options ?? OscWriterOptions.Default;
        int count = GetByteCount(resolved);
        OscWriter spanWriter = new(writer.GetSpan(count)[..count]);
        WriteCore(ref spanWriter, resolved);
        writer.Advance(count);
        return count;
    }

    /// <summary>The packet as a new array, ready to hand to a socket.</summary>
    public byte[] ToArray(OscWriterOptions? options = null)
    {
        OscWriterOptions resolved = options ?? OscWriterOptions.Default;
        byte[] result = new byte[GetByteCount(resolved)];
        OscWriter writer = new(result);
        WriteCore(ref writer, resolved);
        return result;
    }

    public static bool IsBundle(ReadOnlySpan<byte> data) => OscReader.IsBundle(data);

    /// <summary>Decodes a packet, throwing <see cref="OscFormatException"/> when the data is malformed.</summary>
    public static OscPacket Parse(ReadOnlySpan<byte> data, OscReaderOptions? options = null)
        => TryParse(data, out OscPacket? packet, out OscParseError error, options) ? packet : throw new OscFormatException(error);

    /// <summary>Decodes a packet whose blob arguments reference <paramref name="data"/> in place rather than copying.</summary>
    public static OscPacket Parse(ReadOnlyMemory<byte> data, OscReaderOptions? options = null)
        => TryParse(data, out OscPacket? packet, out OscParseError error, options) ? packet : throw new OscFormatException(error);

    public static bool TryParse(ReadOnlySpan<byte> data, [NotNullWhen(true)] out OscPacket? packet, out OscParseError error, OscReaderOptions? options = null)
    {
        int elements = 0;
        return TryParseCore(data, default, options ?? OscReaderOptions.Default, 1, ref elements, out packet, out error);
    }

    /// <summary>Decodes a packet whose blob arguments reference <paramref name="data"/> in place rather than copying.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out OscPacket? packet, out OscParseError error, OscReaderOptions? options = null)
    {
        int elements = 0;
        return TryParseCore(data.Span, data, options ?? OscReaderOptions.Default, 1, ref elements, out packet, out error);
    }

    /// <summary>Decodes a packet from a <c>PipeReader</c> buffer. A single-segment sequence is read in place; otherwise it is copied once.</summary>
    public static OscPacket Parse(in ReadOnlySequence<byte> data, OscReaderOptions? options = null)
        => TryParse(data, out OscPacket? packet, out OscParseError error, options) ? packet : throw new OscFormatException(error);

    /// <summary>Decodes a packet from a <c>PipeReader</c> buffer. A single-segment sequence is read in place; otherwise it is copied once.</summary>
    public static bool TryParse(in ReadOnlySequence<byte> data, [NotNullWhen(true)] out OscPacket? packet, out OscParseError error, OscReaderOptions? options = null)
    {
        if (data.IsSingleSegment)
        {
            return TryParse(data.First, out packet, out error, options);
        }
        if (data.Length > int.MaxValue)
        {
            packet = null;
            error = OscParseError.InvalidLength;
            return false;
        }
        byte[] rented = ArrayPool<byte>.Shared.Rent((int)data.Length);
        try
        {
            data.CopyTo(rented);
            return TryParse(rented.AsSpan(0, (int)data.Length), out packet, out error, options);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool TryParseCore(ReadOnlySpan<byte> data, ReadOnlyMemory<byte> memory, OscReaderOptions options, int depth,
        ref int elementCount, [NotNullWhen(true)] out OscPacket? packet, out OscParseError error)
    {
        packet = null;
        if (data.Length < OscBinary.MinimumMessageLength)
        {
            error = OscParseError.EmptyPacket;
            return false;
        }
        if (data.Length % 4 != 0)
        {
            error = OscParseError.InvalidLength;
            return false;
        }
        // The outermost packet is not an element; everything inside a bundle is.
        if (depth > 1 && ++elementCount > options.MaxBundleElements)
        {
            error = OscParseError.TooManyElements;
            return false;
        }
        OscReader reader = memory.IsEmpty ? new(data, options) : new(memory, options);
        if (reader.IsAtBundle)
        {
            if (depth > options.MaxBundleDepth)
            {
                error = OscParseError.BundleTooDeep;
                return false;
            }
            if (!reader.TryReadBundleHeader(out OscTimeTag timeTag))
            {
                error = reader.Error;
                return false;
            }
            ImmutableArray<OscPacket>.Builder elements = ImmutableArray.CreateBuilder<OscPacket>();
            while (reader.Remaining > 0)
            {
                int offset = reader.Position + 4;
                if (!reader.TryReadBundleElement(out ReadOnlySpan<byte> element))
                {
                    error = reader.Error;
                    return false;
                }
                ReadOnlyMemory<byte> elementMemory = memory.IsEmpty ? default : memory.Slice(offset, element.Length);
                if (!TryParseCore(element, elementMemory, options, depth + 1, ref elementCount, out OscPacket? child, out error))
                {
                    return false;
                }
                elements.Add(child);
            }
            packet = new OscBundle(timeTag, elements.DrainToImmutable());
        }
        else
        {
            if (!reader.TryReadAddress(out ReadOnlySpan<byte> address)
                || !reader.TryReadTypeTags(out ReadOnlySpan<byte> typeTags)
                || !reader.TryReadArguments(typeTags, out ImmutableArray<OscValue> arguments))
            {
                error = reader.Error;
                return false;
            }
            if (reader.Remaining != 0)
            {
                error = OscParseError.TrailingData;
                return false;
            }
            packet = new OscMessage(Encoding.UTF8.GetString(address), arguments);
        }
        error = OscParseError.None;
        return true;
    }

    public abstract override string ToString();
}
