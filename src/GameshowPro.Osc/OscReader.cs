namespace GameshowPro.Osc;

/// <summary>
/// Forward-only, allocation-free decoder over a packet's bytes. Every <c>TryRead</c> method returns false
/// and sets <see cref="Error"/> instead of throwing, so hostile datagrams cost nothing but the check.
/// </summary>
/// <remarks>
/// A receiver that only needs to route can read the address with <see cref="TryReadAddress"/> and compare the
/// UTF-8 bytes without ever creating a string. <see cref="OscPacket.TryParse(ReadOnlySpan{byte}, out OscPacket?, out OscParseError, OscReaderOptions?)"/>
/// builds the full model on top of this type.
/// </remarks>
public ref struct OscReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly ReadOnlyMemory<byte> _memory;
    private int _position;
    private bool _stoppedAtUnknownTag;

    /// <summary>Reads from a span. Blob values are copied out of the span when a model is built.</summary>
    public OscReader(ReadOnlySpan<byte> data, OscReaderOptions? options = null)
    {
        _data = data;
        Options = options ?? OscReaderOptions.Default;
    }

    /// <summary>Reads from memory. Blob values built from it reference the memory in place instead of copying.</summary>
    public OscReader(ReadOnlyMemory<byte> data, OscReaderOptions? options = null) : this(data.Span, options)
    {
        _memory = data;
    }

    public OscReaderOptions Options { get; }

    /// <summary>The first error met, or <see cref="OscParseError.None"/>.</summary>
    public OscParseError Error { get; private set; }

    public readonly int Position => _position;
    public readonly int Remaining => _data.Length - _position;
    public readonly ReadOnlySpan<byte> Unread => _data[_position..];

    /// <summary>True when the bytes at the current position start a bundle.</summary>
    public readonly bool IsAtBundle => IsBundle(Unread);

    public static bool IsBundle(ReadOnlySpan<byte> packet) => packet.StartsWith(OscBinary.BundlePrefix);

    /// <summary>Reads the address pattern: a padded string that starts with <c>/</c> and has no control characters.</summary>
    public bool TryReadAddress(out ReadOnlySpan<byte> address)
    {
        if (!TryReadRawString(out address))
        {
            return false;
        }
        if (address.IsEmpty || address[0] != (byte)'/' || !Utf8.IsValid(address))
        {
            return Fail(OscParseError.InvalidAddress);
        }
        foreach (byte b in address)
        {
            if (b < 0x20 || b == 0x7F)
            {
                return Fail(OscParseError.InvalidAddress);
            }
        }
        return true;
    }

    /// <summary>Reads the type tag string and returns the tags without the leading comma.</summary>
    public bool TryReadTypeTags(out ReadOnlySpan<byte> typeTags)
    {
        if (!TryReadRawString(out ReadOnlySpan<byte> raw))
        {
            typeTags = default;
            return false;
        }
        if (raw.IsEmpty || raw[0] != (byte)',')
        {
            typeTags = default;
            return Fail(OscParseError.MissingTypeTags);
        }
        typeTags = raw[1..];
        return true;
    }

    /// <summary>Reads a padded string argument and returns its UTF-8 bytes, validated but not decoded.</summary>
    public bool TryReadString(out ReadOnlySpan<byte> utf8)
        => TryReadRawString(out utf8) && (Utf8.IsValid(utf8) || Fail(OscParseError.InvalidString));

    public bool TryReadInt32(out int value)
    {
        if (Remaining < 4)
        {
            value = 0;
            return Fail(OscParseError.UnexpectedEnd);
        }
        value = BinaryPrimitives.ReadInt32BigEndian(_data.Slice(_position, 4));
        _position += 4;
        return true;
    }

    public bool TryReadInt64(out long value)
    {
        if (Remaining < 8)
        {
            value = 0;
            return Fail(OscParseError.UnexpectedEnd);
        }
        value = BinaryPrimitives.ReadInt64BigEndian(_data.Slice(_position, 8));
        _position += 8;
        return true;
    }

    public bool TryReadSingle(out float value)
    {
        bool read = TryReadInt32(out int bits);
        value = BitConverter.Int32BitsToSingle(bits);
        return read;
    }

    public bool TryReadDouble(out double value)
    {
        bool read = TryReadInt64(out long bits);
        value = BitConverter.Int64BitsToDouble(bits);
        return read;
    }

    public bool TryReadChar(out char value)
    {
        bool read = TryReadInt32(out int bits);
        value = (char)bits;
        return read;
    }

    public bool TryReadTimeTag(out OscTimeTag value)
    {
        bool read = TryReadInt64(out long bits);
        value = new(unchecked((ulong)bits));
        return read;
    }

    public bool TryReadColor(out OscColor value)
    {
        bool read = TryReadInt32(out int bits);
        value = OscColor.FromPacked(unchecked((uint)bits));
        return read;
    }

    public bool TryReadMidi(out OscMidiMessage value)
    {
        bool read = TryReadInt32(out int bits);
        value = OscMidiMessage.FromPacked(unchecked((uint)bits));
        return read;
    }

    /// <summary>Reads a blob and returns its bytes, which alias the packet.</summary>
    public bool TryReadBlob(out ReadOnlySpan<byte> blob)
    {
        blob = default;
        if (!TryReadInt32(out int length))
        {
            return false;
        }
        if (length < 0 || length > Remaining)
        {
            return Fail(OscParseError.InvalidBlobLength);
        }
        int padded = OscBinary.Align(length);
        if (padded > Remaining)
        {
            return Fail(OscParseError.UnexpectedEnd);
        }
        if (_data.Slice(_position + length, padded - length).ContainsAnyExcept((byte)0))
        {
            return Fail(OscParseError.InvalidPadding);
        }
        blob = _data.Slice(_position, length);
        _position += padded;
        return true;
    }

    /// <summary>Reads <c>#bundle</c> and the time tag.</summary>
    public bool TryReadBundleHeader(out OscTimeTag timeTag)
    {
        timeTag = default;
        if (Remaining < OscBinary.BundleHeaderLength || !IsAtBundle)
        {
            return Fail(OscParseError.InvalidBundleHeader);
        }
        _position += OscBinary.BundlePrefix.Length;
        return TryReadTimeTag(out timeTag);
    }

    /// <summary>Reads one bundle element: its size prefix, then the element bytes, which alias the packet.</summary>
    public bool TryReadBundleElement(out ReadOnlySpan<byte> element)
    {
        element = default;
        if (!TryReadInt32(out int size))
        {
            return false;
        }
        if (size < 4 || size % 4 != 0 || size > Remaining)
        {
            return Fail(OscParseError.InvalidElementSize);
        }
        element = _data.Slice(_position, size);
        _position += size;
        return true;
    }

    /// <summary>Reads every argument described by <paramref name="typeTags"/> (without the comma).</summary>
    /// <remarks>
    /// Under <see cref="OscUnknownTypeTagHandling.AcceptOpaque"/> decoding stops at the first unknown top-level
    /// tag and this returns true with the arguments read so far, leaving the rest of the message in
    /// <see cref="Unread"/>. Use the overload with <c>opaqueTypeTags</c> to learn where it stopped.
    /// </remarks>
    public bool TryReadArguments(ReadOnlySpan<byte> typeTags, out ImmutableArray<OscValue> arguments)
        => TryReadArguments(typeTags, out arguments, out _);

    /// <summary>
    /// Reads every argument described by <paramref name="typeTags"/> (without the comma). Under
    /// <see cref="OscUnknownTypeTagHandling.AcceptOpaque"/> decoding stops at the first unknown top-level tag:
    /// <paramref name="opaqueTypeTags"/> then holds that tag and every tag after it, and <see cref="Unread"/>
    /// holds the bytes they describe. Otherwise <paramref name="opaqueTypeTags"/> is empty.
    /// </summary>
    public bool TryReadArguments(ReadOnlySpan<byte> typeTags, out ImmutableArray<OscValue> arguments, out ReadOnlySpan<byte> opaqueTypeTags)
    {
        opaqueTypeTags = default;
        if (typeTags.IsEmpty)
        {
            arguments = [];
            return true;
        }
        // Each top-level tag produces at most one value, so this capacity is exact for messages without arrays.
        ImmutableArray<OscValue>.Builder builder = ImmutableArray.CreateBuilder<OscValue>(typeTags.Length);
        int index = 0;
        while (index < typeTags.Length)
        {
            if (!TryReadValue(typeTags, ref index, inArray: false, out OscValue value))
            {
                if (ConsumeUnknownTagStop())
                {
                    opaqueTypeTags = typeTags[index..];
                    break;
                }
                arguments = default;
                return false;
            }
            builder.Add(value);
        }
        arguments = builder.DrainToImmutable();
        return true;
    }

    /// <summary>
    /// Reads the argument for the tag at <paramref name="typeTags"/>[<paramref name="index"/>] and advances
    /// <paramref name="index"/> past it, including over a whole array.
    /// </summary>
    /// <remarks>
    /// A single value has no "rest of the message" to keep opaque, so an unknown tag fails with
    /// <see cref="OscParseError.UnsupportedTypeTag"/> whatever <see cref="OscReaderOptions.UnknownTypeTags"/>
    /// says; <see cref="TryReadArguments(ReadOnlySpan{byte}, out ImmutableArray{OscValue}, out ReadOnlySpan{byte})"/>
    /// applies that option.
    /// </remarks>
    public bool TryReadValue(ReadOnlySpan<byte> typeTags, ref int index, out OscValue value)
    {
        if (TryReadValue(typeTags, ref index, inArray: false, out value))
        {
            return true;
        }
        if (ConsumeUnknownTagStop())
        {
            index++;
            _ = Fail(OscParseError.UnsupportedTypeTag);
        }
        return false;
    }

    /// <summary>
    /// Walks every argument described by <paramref name="typeTags"/>, checking each one's framing without
    /// building a value or decoding a string. Stops at an unknown top-level tag exactly as
    /// <see cref="TryReadArguments(ReadOnlySpan{byte}, out ImmutableArray{OscValue}, out ReadOnlySpan{byte})"/> does.
    /// </summary>
    internal bool TrySkipArguments(ReadOnlySpan<byte> typeTags, out ReadOnlySpan<byte> opaqueTypeTags)
    {
        opaqueTypeTags = default;
        int index = 0;
        while (index < typeTags.Length)
        {
            if (!TrySkipValue(typeTags, ref index, inArray: false))
            {
                if (ConsumeUnknownTagStop())
                {
                    opaqueTypeTags = typeTags[index..];
                    return true;
                }
                return false;
            }
        }
        return true;
    }

    private bool TryReadValue(ReadOnlySpan<byte> typeTags, ref int index, bool inArray, out OscValue value)
    {
        value = default;
        if ((uint)index >= (uint)typeTags.Length)
        {
            return Fail(OscParseError.InvalidArray);
        }
        byte tag = typeTags[index++];
        switch (tag)
        {
            case (byte)OscTypeTag.Int32:
            case (byte)OscTypeTag.Float32:
            case (byte)OscTypeTag.Char:
            case (byte)OscTypeTag.Color:
            case (byte)OscTypeTag.Midi:
            case (byte)'M': // The Ventuz OSC node library writes MIDI messages with an upper-case tag.
                {
                    if (!TryReadInt32(out int bits))
                    {
                        return false;
                    }
                    value = tag switch
                    {
                        (byte)OscTypeTag.Int32 => OscValue.FromInt32(bits),
                        (byte)OscTypeTag.Float32 => OscValue.FromSingle(BitConverter.Int32BitsToSingle(bits)),
                        (byte)OscTypeTag.Char => OscValue.FromChar((char)bits),
                        (byte)OscTypeTag.Color => OscValue.FromColor(OscColor.FromPacked(unchecked((uint)bits))),
                        _ => OscValue.FromMidi(OscMidiMessage.FromPacked(unchecked((uint)bits))),
                    };
                    return true;
                }
            case (byte)OscTypeTag.Int64:
            case (byte)OscTypeTag.TimeTag:
            case (byte)OscTypeTag.Double:
                {
                    if (!TryReadInt64(out long bits))
                    {
                        return false;
                    }
                    value = tag switch
                    {
                        (byte)OscTypeTag.Int64 => OscValue.FromInt64(bits),
                        (byte)OscTypeTag.TimeTag => OscValue.FromTimeTag(new(unchecked((ulong)bits))),
                        _ => OscValue.FromDouble(BitConverter.Int64BitsToDouble(bits)),
                    };
                    return true;
                }
            case (byte)OscTypeTag.String:
            case (byte)OscTypeTag.Symbol:
                {
                    if (!TryReadString(out ReadOnlySpan<byte> utf8))
                    {
                        return false;
                    }
                    if (inArray && Options.ArrayEncoding == OscArrayEncoding.VentuzRemoting)
                    {
                        if (utf8.Length == 1 && utf8[0] == (byte)'\\')
                        {
                            value = OscValue.Nil;
                            return true;
                        }
                        if (utf8.Length >= 2 && utf8[0] == (byte)'\\' && utf8[1] == (byte)'\\')
                        {
                            utf8 = utf8[1..];
                        }
                    }
                    string text = Encoding.UTF8.GetString(utf8);
                    value = tag == (byte)OscTypeTag.String ? OscValue.FromString(text) : OscValue.FromSymbol(text);
                    return true;
                }
            case (byte)OscTypeTag.Blob:
                {
                    int start = _position + 4;
                    if (!TryReadBlob(out ReadOnlySpan<byte> blob))
                    {
                        return false;
                    }
                    value = OscValue.FromBlob(_memory.IsEmpty ? blob.ToArray() : _memory.Slice(start, blob.Length));
                    return true;
                }
            case (byte)OscTypeTag.BitVector:
                {
                    if (!TryReadBlob(out ReadOnlySpan<byte> blob))
                    {
                        return false;
                    }
                    if (!OscBitVector.TryDecode(blob, out ImmutableArray<bool> bits))
                    {
                        return Fail(OscParseError.InvalidBitVector);
                    }
                    value = OscValue.FromBitVector(bits);
                    return true;
                }
            case (byte)OscTypeTag.True:
                value = OscValue.True;
                return true;
            case (byte)OscTypeTag.False:
                value = OscValue.False;
                return true;
            case (byte)OscTypeTag.Nil:
                value = OscValue.Nil;
                return true;
            case (byte)OscTypeTag.Infinitum:
                value = OscValue.Infinitum;
                return true;
            case (byte)OscTypeTag.ArrayBegin:
                return Options.ArrayEncoding == OscArrayEncoding.VentuzRemoting
                    ? TryReadVentuzArray(typeTags, ref index, inArray, out value)
                    : TryReadStandardArray(typeTags, ref index, out value);
            case (byte)OscTypeTag.ArrayEnd:
                return Fail(OscParseError.InvalidArray);
            default:
                return FailUnknownTag(ref index, inArray);
        }
    }

    /// <summary>The framing-only counterpart of <see cref="TryReadValue(ReadOnlySpan{byte}, ref int, bool, out OscValue)"/>; the two switches must list the same tags.</summary>
    private bool TrySkipValue(ReadOnlySpan<byte> typeTags, ref int index, bool inArray)
    {
        if ((uint)index >= (uint)typeTags.Length)
        {
            return Fail(OscParseError.InvalidArray);
        }
        byte tag = typeTags[index++];
        switch (tag)
        {
            case (byte)OscTypeTag.Int32:
            case (byte)OscTypeTag.Float32:
            case (byte)OscTypeTag.Char:
            case (byte)OscTypeTag.Color:
            case (byte)OscTypeTag.Midi:
            case (byte)'M':
                return TryReadInt32(out _);
            case (byte)OscTypeTag.Int64:
            case (byte)OscTypeTag.TimeTag:
            case (byte)OscTypeTag.Double:
                return TryReadInt64(out _);
            case (byte)OscTypeTag.String:
            case (byte)OscTypeTag.Symbol:
                return TryReadString(out _);
            case (byte)OscTypeTag.Blob:
            case (byte)OscTypeTag.BitVector: // Every well-framed blob is a valid bit vector, so framing is the whole check.
                return TryReadBlob(out _);
            case (byte)OscTypeTag.True:
            case (byte)OscTypeTag.False:
            case (byte)OscTypeTag.Nil:
            case (byte)OscTypeTag.Infinitum:
                return true;
            case (byte)OscTypeTag.ArrayBegin:
                return Options.ArrayEncoding == OscArrayEncoding.VentuzRemoting
                    ? TrySkipVentuzArray(typeTags, ref index, inArray)
                    : TrySkipStandardArray(typeTags, ref index);
            case (byte)OscTypeTag.ArrayEnd:
                return Fail(OscParseError.InvalidArray);
            default:
                return FailUnknownTag(ref index, inArray);
        }
    }

    /// <summary>
    /// An unknown tag ends decoding under <see cref="OscUnknownTypeTagHandling.AcceptOpaque"/>, but only at the
    /// top level: inside an array the closing bracket cannot be found without knowing every element's size.
    /// </summary>
    private bool FailUnknownTag(ref int index, bool inArray)
    {
        if (!inArray && Options.UnknownTypeTags == OscUnknownTypeTagHandling.AcceptOpaque)
        {
            // Leave the tag in place: it and everything after it belong to the opaque tail.
            index--;
            _stoppedAtUnknownTag = true;
            return false;
        }
        return Fail(OscParseError.UnsupportedTypeTag);
    }

    private bool ConsumeUnknownTagStop()
    {
        bool stopped = _stoppedAtUnknownTag;
        _stoppedAtUnknownTag = false;
        return stopped;
    }

    private bool TrySkipStandardArray(ReadOnlySpan<byte> typeTags, ref int index)
    {
        while (true)
        {
            if (index >= typeTags.Length)
            {
                return Fail(OscParseError.InvalidArray);
            }
            if (typeTags[index] == (byte)OscTypeTag.ArrayEnd)
            {
                index++;
                return true;
            }
            if (!TrySkipValue(typeTags, ref index, inArray: true))
            {
                return false;
            }
        }
    }

    private bool TrySkipVentuzArray(ReadOnlySpan<byte> typeTags, ref int index, bool inArray)
    {
        if (!TryReadVentuzArrayHeader(typeTags, ref index, inArray, out int count))
        {
            return false;
        }
        ReadOnlySpan<byte> elementTags = typeTags.Slice(index - 2, 1);
        for (int i = 0; i < count; i++)
        {
            int elementIndex = 0;
            if (!TrySkipValue(elementTags, ref elementIndex, inArray: true))
            {
                return false;
            }
        }
        return true;
    }

    private bool TryReadStandardArray(ReadOnlySpan<byte> typeTags, ref int index, out OscValue value)
    {
        value = default;
        ImmutableArray<OscValue>.Builder builder = ImmutableArray.CreateBuilder<OscValue>();
        while (true)
        {
            if (index >= typeTags.Length)
            {
                return Fail(OscParseError.InvalidArray);
            }
            if (typeTags[index] == (byte)OscTypeTag.ArrayEnd)
            {
                index++;
                break;
            }
            if (!TryReadValue(typeTags, ref index, inArray: true, out OscValue element))
            {
                return false;
            }
            builder.Add(element);
        }
        value = OscValue.FromArray(builder.DrainToImmutable());
        return true;
    }

    /// <summary>
    /// Ventuz types the array, not its elements: the tags are <c>[</c>, one element tag, <c>]</c>, and the data
    /// is an int32 byte count, an int32 element count and the elements. The byte count is not checked, because
    /// the element count and the per-element decoding already bound the read.
    /// </summary>
    private bool TryReadVentuzArray(ReadOnlySpan<byte> typeTags, ref int index, bool inArray, out OscValue value)
    {
        value = default;
        if (!TryReadVentuzArrayHeader(typeTags, ref index, inArray, out int count))
        {
            return false;
        }
        ReadOnlySpan<byte> elementTags = typeTags.Slice(index - 2, 1);
        byte elementTag = elementTags[0];
        ImmutableArray<OscValue>.Builder builder = ImmutableArray.CreateBuilder<OscValue>(count);
        for (int i = 0; i < count; i++)
        {
            int elementIndex = 0;
            if (!TryReadValue(elementTags, ref elementIndex, inArray: true, out OscValue element))
            {
                return false;
            }
            builder.Add(element);
        }
        value = OscValue.FromArray(builder.DrainToImmutable(), (OscTypeTag)elementTag);
        return true;
    }

    /// <summary>Validates the <c>[x]</c> tags of a Ventuz array, advances past them and reads the two count words.</summary>
    private bool TryReadVentuzArrayHeader(ReadOnlySpan<byte> typeTags, ref int index, bool inArray, out int count)
    {
        count = 0;
        if (inArray || typeTags.Length - index < 2 || typeTags[index + 1] != (byte)OscTypeTag.ArrayEnd)
        {
            return Fail(OscParseError.InvalidArray);
        }
        if (typeTags[index] is (byte)OscTypeTag.ArrayBegin or (byte)OscTypeTag.Nil)
        {
            return Fail(OscParseError.InvalidArray);
        }
        index += 2;
        if (!TryReadInt32(out _) || !TryReadInt32(out count))
        {
            return false;
        }
        if (count < 0 || count > Remaining)
        {
            return Fail(OscParseError.InvalidArray);
        }
        return true;
    }

    private bool TryReadRawString(out ReadOnlySpan<byte> value)
    {
        ReadOnlySpan<byte> rest = Unread;
        int end = rest.IndexOf((byte)0);
        if (end < 0)
        {
            value = default;
            return Fail(OscParseError.UnexpectedEnd);
        }
        int consumed = OscBinary.Align(end + 1);
        if (consumed > rest.Length)
        {
            value = default;
            return Fail(OscParseError.UnexpectedEnd);
        }
        if (rest[(end + 1)..consumed].ContainsAnyExcept((byte)0))
        {
            value = default;
            return Fail(OscParseError.InvalidPadding);
        }
        value = rest[..end];
        _position += consumed;
        return true;
    }

    private bool Fail(OscParseError error)
    {
        if (Error == OscParseError.None)
        {
            Error = error;
        }
        return false;
    }
}
