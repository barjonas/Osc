namespace GameshowPro.Osc;

/// <summary>
/// One OSC argument: a tagged union over every standard type plus the Ventuz bit vector. Sixteen bytes,
/// and numeric, boolean, nil, infinitum, time tag, character, colour and MIDI values hold no heap reference,
/// so building and reading messages made of them does not allocate per argument.
/// </summary>
/// <remarks>
/// Equality is wire equality: two values are equal when they have the same tag and would encode to the same
/// bytes. Floating-point values therefore compare by bit pattern, so <c>NaN == NaN</c> and <c>0.0 != -0.0</c>.
/// <see langword="default"/> is <see cref="Nil"/>.
/// </remarks>
public readonly struct OscValue : IEquatable<OscValue>
{
    private const string VentuzNullString = "\\";

    private readonly long _bits;
    private readonly object? _reference;
    private readonly byte _tag;

    private OscValue(OscTypeTag tag, long bits = 0, object? reference = null)
    {
        _tag = (byte)tag;
        _bits = bits;
        _reference = reference;
    }

    /// <summary>The type tag. For an array this is <see cref="OscTypeTag.ArrayBegin"/>.</summary>
    public OscTypeTag Tag => _tag == 0 ? OscTypeTag.Nil : (OscTypeTag)_tag;

    public static OscValue Nil => default;
    public static OscValue Infinitum { get; } = new(OscTypeTag.Infinitum);
    public static OscValue True { get; } = new(OscTypeTag.True);
    public static OscValue False { get; } = new(OscTypeTag.False);

    public static OscValue FromBoolean(bool value) => value ? True : False;
    public static OscValue FromInt32(int value) => new(OscTypeTag.Int32, value);
    public static OscValue FromInt64(long value) => new(OscTypeTag.Int64, value);
    public static OscValue FromSingle(float value) => new(OscTypeTag.Float32, BitConverter.SingleToInt32Bits(value));
    public static OscValue FromDouble(double value) => new(OscTypeTag.Double, BitConverter.DoubleToInt64Bits(value));
    public static OscValue FromChar(char value) => new(OscTypeTag.Char, value);
    public static OscValue FromTimeTag(OscTimeTag value) => new(OscTypeTag.TimeTag, unchecked((long)value.Value));
    public static OscValue FromColor(OscColor value) => new(OscTypeTag.Color, value.ToPacked());
    public static OscValue FromMidi(OscMidiMessage value) => new(OscTypeTag.Midi, value.ToPacked());
    public static OscValue FromString(string value) => new(OscTypeTag.String, 0, ValidateString(value));
    public static OscValue FromSymbol(string value) => new(OscTypeTag.Symbol, 0, ValidateString(value));

    /// <summary>
    /// Wraps <paramref name="value"/> without copying. Array-backed and <see cref="MemoryManager{T}"/>-backed
    /// memory is referenced in place; the caller must not change the bytes afterwards.
    /// </summary>
    public static OscValue FromBlob(ReadOnlyMemory<byte> value)
    {
        if (MemoryMarshal.TryGetArray(value, out ArraySegment<byte> segment))
        {
            return new(OscTypeTag.Blob, Pack(segment.Offset, segment.Count), segment.Array);
        }
        if (MemoryMarshal.TryGetMemoryManager(value, out MemoryManager<byte>? manager, out int start, out int length))
        {
            return new(OscTypeTag.Blob, Pack(start, length), manager);
        }
        return new(OscTypeTag.Blob);
    }

    public static OscValue FromBlob(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(OscTypeTag.Blob, Pack(0, value.Length), value);
    }

    /// <summary>
    /// An array of values, for example <c>OscValue.FromArray([1, 2.5f, "x"])</c>. <paramref name="elementTag"/> is used
    /// only by the Ventuz dialect, which types the array rather than each element; when omitted it is inferred from
    /// the first element.
    /// </summary>
    public static OscValue FromArray(ImmutableArray<OscValue> elements, OscTypeTag? elementTag = null)
        => new(OscTypeTag.ArrayBegin, elementTag is { } tag ? (byte)tag : 0, elements.IsDefaultOrEmpty ? Array.Empty<OscValue>() : ImmutableCollectionsMarshal.AsArray(elements));

    /// <summary>A Ventuz bit vector, for example <c>OscValue.FromBitVector([true, false])</c>.</summary>
    public static OscValue FromBitVector(ImmutableArray<bool> bits)
        => new(OscTypeTag.BitVector, 0, bits.IsDefaultOrEmpty ? Array.Empty<bool>() : ImmutableCollectionsMarshal.AsArray(bits));

    public static implicit operator OscValue(bool value) => FromBoolean(value);
    public static implicit operator OscValue(int value) => FromInt32(value);
    public static implicit operator OscValue(long value) => FromInt64(value);
    public static implicit operator OscValue(float value) => FromSingle(value);
    public static implicit operator OscValue(double value) => FromDouble(value);
    public static implicit operator OscValue(char value) => FromChar(value);
    public static implicit operator OscValue(string value) => FromString(value);
    public static implicit operator OscValue(byte[] value) => FromBlob(value);
    public static implicit operator OscValue(ReadOnlyMemory<byte> value) => FromBlob(value);
    public static implicit operator OscValue(OscTimeTag value) => FromTimeTag(value);
    public static implicit operator OscValue(OscColor value) => FromColor(value);
    public static implicit operator OscValue(OscMidiMessage value) => FromMidi(value);
    public static implicit operator OscValue(ImmutableArray<OscValue> value) => FromArray(value);
    public static implicit operator OscValue(ImmutableArray<bool> value) => FromBitVector(value);

    public bool IsNil => Tag == OscTypeTag.Nil;
    public bool IsString => Tag is OscTypeTag.String or OscTypeTag.Symbol;
    public bool IsBoolean => Tag is OscTypeTag.True or OscTypeTag.False;
    public bool IsNumber => Tag is OscTypeTag.Int32 or OscTypeTag.Int64 or OscTypeTag.Float32 or OscTypeTag.Double;
    public bool IsArray => Tag == OscTypeTag.ArrayBegin;

    public bool TryGetInt32(out int value) => TryGetBits(OscTypeTag.Int32, out value);
    public bool TryGetInt64(out long value) => TryGetBits(OscTypeTag.Int64, out value);

    public bool TryGetSingle(out float value)
    {
        bool found = TryGetBits(OscTypeTag.Float32, out int bits);
        value = BitConverter.Int32BitsToSingle(bits);
        return found;
    }

    public bool TryGetDouble(out double value)
    {
        bool found = TryGetBits(OscTypeTag.Double, out long bits);
        value = BitConverter.Int64BitsToDouble(bits);
        return found;
    }

    public bool TryGetChar(out char value)
    {
        bool found = TryGetBits(OscTypeTag.Char, out int bits);
        value = (char)bits;
        return found;
    }

    public bool TryGetTimeTag(out OscTimeTag value)
    {
        bool found = TryGetBits(OscTypeTag.TimeTag, out long bits);
        value = new(unchecked((ulong)bits));
        return found;
    }

    public bool TryGetColor(out OscColor value)
    {
        bool found = TryGetBits(OscTypeTag.Color, out int bits);
        value = OscColor.FromPacked(unchecked((uint)bits));
        return found;
    }

    public bool TryGetMidi(out OscMidiMessage value)
    {
        bool found = TryGetBits(OscTypeTag.Midi, out int bits);
        value = OscMidiMessage.FromPacked(unchecked((uint)bits));
        return found;
    }

    public bool TryGetBoolean(out bool value)
    {
        value = Tag == OscTypeTag.True;
        return IsBoolean;
    }

    /// <summary>Gets a string or symbol.</summary>
    public bool TryGetString([NotNullWhen(true)] out string? value)
    {
        value = IsString ? (string)_reference! : null;
        return value is not null;
    }

    public bool TryGetBlob(out ReadOnlyMemory<byte> value)
    {
        value = Tag == OscTypeTag.Blob ? BlobMemory : default;
        return Tag == OscTypeTag.Blob;
    }

    public bool TryGetArray(out ImmutableArray<OscValue> value)
    {
        value = IsArray ? ImmutableCollectionsMarshal.AsImmutableArray(ArrayElements) : default;
        return IsArray;
    }

    public bool TryGetBitVector(out ImmutableArray<bool> value)
    {
        value = Tag == OscTypeTag.BitVector ? ImmutableCollectionsMarshal.AsImmutableArray(BitVectorBits) : default;
        return Tag == OscTypeTag.BitVector;
    }

    /// <summary>Converts any of the four numeric types to a double.</summary>
    public bool TryGetNumber(out double value)
    {
        switch (Tag)
        {
            case OscTypeTag.Int32:
                value = (int)_bits;
                return true;
            case OscTypeTag.Int64:
                value = _bits;
                return true;
            case OscTypeTag.Float32:
                value = BitConverter.Int32BitsToSingle((int)_bits);
                return true;
            case OscTypeTag.Double:
                value = BitConverter.Int64BitsToDouble(_bits);
                return true;
            default:
                value = 0;
                return false;
        }
    }

    public int AsInt32() => TryGetInt32(out int value) ? value : throw Mismatch(OscTypeTag.Int32);
    public long AsInt64() => TryGetInt64(out long value) ? value : throw Mismatch(OscTypeTag.Int64);
    public float AsSingle() => TryGetSingle(out float value) ? value : throw Mismatch(OscTypeTag.Float32);
    public double AsDouble() => TryGetDouble(out double value) ? value : throw Mismatch(OscTypeTag.Double);
    public char AsChar() => TryGetChar(out char value) ? value : throw Mismatch(OscTypeTag.Char);
    public OscTimeTag AsTimeTag() => TryGetTimeTag(out OscTimeTag value) ? value : throw Mismatch(OscTypeTag.TimeTag);
    public OscColor AsColor() => TryGetColor(out OscColor value) ? value : throw Mismatch(OscTypeTag.Color);
    public OscMidiMessage AsMidi() => TryGetMidi(out OscMidiMessage value) ? value : throw Mismatch(OscTypeTag.Midi);
    public bool AsBoolean() => TryGetBoolean(out bool value) ? value : throw Mismatch(OscTypeTag.True);
    public string AsString() => TryGetString(out string? value) ? value : throw Mismatch(OscTypeTag.String);
    public ReadOnlyMemory<byte> AsBlob() => TryGetBlob(out ReadOnlyMemory<byte> value) ? value : throw Mismatch(OscTypeTag.Blob);
    public ImmutableArray<OscValue> AsArray() => TryGetArray(out ImmutableArray<OscValue> value) ? value : throw Mismatch(OscTypeTag.ArrayBegin);
    public ImmutableArray<bool> AsBitVector() => TryGetBitVector(out ImmutableArray<bool> value) ? value : throw Mismatch(OscTypeTag.BitVector);

    /// <summary>
    /// The value boxed as its natural CLR type: <see cref="int"/>, <see cref="long"/>, <see cref="float"/>,
    /// <see cref="double"/>, <see cref="char"/>, <see cref="string"/>, <see cref="bool"/>, <c>ReadOnlyMemory&lt;byte&gt;</c>,
    /// <see cref="OscTimeTag"/>, <see cref="OscColor"/>, <see cref="OscMidiMessage"/>, <c>ImmutableArray&lt;OscValue&gt;</c>,
    /// <c>ImmutableArray&lt;bool&gt;</c>, <see cref="float.PositiveInfinity"/> for infinitum, or <see langword="null"/> for nil.
    /// </summary>
    public object? ToObject() => Tag switch
    {
        OscTypeTag.Int32 => (int)_bits,
        OscTypeTag.Float32 => BitConverter.Int32BitsToSingle((int)_bits),
        OscTypeTag.String or OscTypeTag.Symbol => (string)_reference!,
        OscTypeTag.Blob => BlobMemory,
        OscTypeTag.Int64 => _bits,
        OscTypeTag.TimeTag => new OscTimeTag(unchecked((ulong)_bits)),
        OscTypeTag.Double => BitConverter.Int64BitsToDouble(_bits),
        OscTypeTag.Char => (char)_bits,
        OscTypeTag.Color => OscColor.FromPacked(unchecked((uint)_bits)),
        OscTypeTag.Midi => OscMidiMessage.FromPacked(unchecked((uint)_bits)),
        OscTypeTag.True => true,
        OscTypeTag.False => false,
        OscTypeTag.Nil => null,
        OscTypeTag.Infinitum => float.PositiveInfinity,
        OscTypeTag.ArrayBegin => ImmutableCollectionsMarshal.AsImmutableArray(ArrayElements),
        OscTypeTag.BitVector => ImmutableCollectionsMarshal.AsImmutableArray(BitVectorBits),
        OscTypeTag.ArrayEnd => throw new UnreachableException(),
        _ => throw new UnreachableException(),
    };

    public bool Equals(OscValue other)
    {
        if (Tag != other.Tag)
        {
            return false;
        }
        switch (Tag)
        {
            case OscTypeTag.String:
            case OscTypeTag.Symbol:
                return string.Equals((string)_reference!, (string)other._reference!, StringComparison.Ordinal);
            case OscTypeTag.Blob:
                return BlobMemory.Span.SequenceEqual(other.BlobMemory.Span);
            case OscTypeTag.ArrayBegin:
                return ArrayElements.AsSpan().SequenceEqual(other.ArrayElements);
            case OscTypeTag.BitVector:
                return BitVectorBits.AsSpan().SequenceEqual(other.BitVectorBits);
            case OscTypeTag.Int32:
            case OscTypeTag.Float32:
            case OscTypeTag.Int64:
            case OscTypeTag.TimeTag:
            case OscTypeTag.Double:
            case OscTypeTag.Char:
            case OscTypeTag.Color:
            case OscTypeTag.Midi:
                return _bits == other._bits;
            case OscTypeTag.True:
            case OscTypeTag.False:
            case OscTypeTag.Nil:
            case OscTypeTag.Infinitum:
            case OscTypeTag.ArrayEnd:
                return true;
            default:
                throw new UnreachableException();
        }
    }

    public override bool Equals(object? obj) => obj is OscValue other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Tag);
        switch (Tag)
        {
            case OscTypeTag.String:
            case OscTypeTag.Symbol:
                hash.Add((string)_reference!, StringComparer.Ordinal);
                break;
            case OscTypeTag.Blob:
                hash.AddBytes(BlobMemory.Span);
                break;
            case OscTypeTag.ArrayBegin:
                foreach (OscValue element in ArrayElements)
                {
                    hash.Add(element);
                }
                break;
            case OscTypeTag.BitVector:
                foreach (bool bit in BitVectorBits)
                {
                    hash.Add(bit);
                }
                break;
            case OscTypeTag.Int32:
            case OscTypeTag.Float32:
            case OscTypeTag.Int64:
            case OscTypeTag.TimeTag:
            case OscTypeTag.Double:
            case OscTypeTag.Char:
            case OscTypeTag.Color:
            case OscTypeTag.Midi:
                hash.Add(_bits);
                break;
            case OscTypeTag.True:
            case OscTypeTag.False:
            case OscTypeTag.Nil:
            case OscTypeTag.Infinitum:
            case OscTypeTag.ArrayEnd:
                break;
            default:
                throw new UnreachableException();
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(OscValue left, OscValue right) => left.Equals(right);
    public static bool operator !=(OscValue left, OscValue right) => !left.Equals(right);

    public override string ToString() => Tag switch
    {
        OscTypeTag.Int32 => ((int)_bits).ToString(CultureInfo.InvariantCulture),
        OscTypeTag.Float32 => BitConverter.Int32BitsToSingle((int)_bits).ToString("R", CultureInfo.InvariantCulture) + "f",
        OscTypeTag.String => "\"" + (string)_reference! + "\"",
        OscTypeTag.Symbol => "'" + (string)_reference! + "'",
        OscTypeTag.Blob => $"blob[{BlobLength}]",
        OscTypeTag.Int64 => _bits.ToString(CultureInfo.InvariantCulture) + "L",
        OscTypeTag.TimeTag => new OscTimeTag(unchecked((ulong)_bits)).ToString(),
        OscTypeTag.Double => BitConverter.Int64BitsToDouble(_bits).ToString("R", CultureInfo.InvariantCulture) + "d",
        OscTypeTag.Char => "'" + (char)_bits + "'",
        OscTypeTag.Color => OscColor.FromPacked(unchecked((uint)_bits)).ToString(),
        OscTypeTag.Midi => OscMidiMessage.FromPacked(unchecked((uint)_bits)).ToString(),
        OscTypeTag.True => "true",
        OscTypeTag.False => "false",
        OscTypeTag.Nil => "nil",
        OscTypeTag.Infinitum => "infinitum",
        OscTypeTag.ArrayBegin => "[" + string.Join(", ", ArrayElements) + "]",
        OscTypeTag.BitVector => "bits[" + string.Concat(BitVectorBits.Select(bit => bit ? '1' : '0')) + "]",
        OscTypeTag.ArrayEnd => throw new UnreachableException(),
        _ => throw new UnreachableException(),
    };

    // ----- Encoding -----

    /// <summary>The element tag a Ventuz array is written with: the explicit hint, else the first element's tag.</summary>
    internal OscTypeTag? ArrayElementTag
    {
        get
        {
            if (_bits != 0)
            {
                return (OscTypeTag)(byte)_bits;
            }
            OscValue[] elements = ArrayElements;
            return elements.Length == 0 ? null : VentuzElementTag(elements[0]);
        }
    }

    private static OscTypeTag VentuzElementTag(OscValue element) => element.Tag == OscTypeTag.Nil ? OscTypeTag.String : element.Tag;

    /// <summary>Number of type tag characters this value contributes, counting both array brackets.</summary>
    internal int GetTypeTagCount(OscWriterOptions options)
    {
        if (!IsArray)
        {
            return 1;
        }
        if (options.ArrayEncoding == OscArrayEncoding.VentuzRemoting)
        {
            return 3;
        }
        int count = 2;
        foreach (OscValue element in ArrayElements)
        {
            count += element.GetTypeTagCount(options);
        }
        return count;
    }

    internal void WriteTypeTags(ref OscWriter writer, OscWriterOptions options, bool inArray)
    {
        if (IsArray)
        {
            writer.WriteByte((byte)OscTypeTag.ArrayBegin);
            if (options.ArrayEncoding == OscArrayEncoding.VentuzRemoting)
            {
                writer.WriteByte((byte)RequireVentuzElementTag(inArray));
            }
            else
            {
                foreach (OscValue element in ArrayElements)
                {
                    element.WriteTypeTags(ref writer, options, inArray: true);
                }
            }
            writer.WriteByte((byte)OscTypeTag.ArrayEnd);
            return;
        }
        writer.WriteByte((byte)(inArray && options.ArrayEncoding == OscArrayEncoding.VentuzRemoting ? VentuzElementTag(this) : Tag));
    }

    /// <summary>Bytes this value's argument data occupies, including padding.</summary>
    internal int GetByteCount(OscWriterOptions options, bool inArray)
    {
        bool ventuzArray = inArray && options.ArrayEncoding == OscArrayEncoding.VentuzRemoting;
        switch (Tag)
        {
            case OscTypeTag.Int32:
            case OscTypeTag.Float32:
            case OscTypeTag.Char:
            case OscTypeTag.Color:
            case OscTypeTag.Midi:
                return 4;
            case OscTypeTag.Int64:
            case OscTypeTag.TimeTag:
            case OscTypeTag.Double:
                return 8;
            case OscTypeTag.True:
            case OscTypeTag.False:
            case OscTypeTag.Infinitum:
                return 0;
            case OscTypeTag.Nil:
                return ventuzArray ? OscBinary.PaddedStringLength(VentuzNullString.Length) : 0;
            case OscTypeTag.String:
            case OscTypeTag.Symbol:
                return OscBinary.PaddedStringLength(StringByteCount((string)_reference!, options, ventuzArray));
            case OscTypeTag.Blob:
                return 4 + OscBinary.Align(BlobLength);
            case OscTypeTag.BitVector:
                return 4 + OscBinary.Align(OscBitVector.EncodedLength(BitVectorBits.Length));
            case OscTypeTag.ArrayBegin:
                {
                    int total = options.ArrayEncoding == OscArrayEncoding.VentuzRemoting ? 8 : 0;
                    foreach (OscValue element in ArrayElements)
                    {
                        total += element.GetByteCount(options, inArray: true);
                    }
                    return total;
                }
            case OscTypeTag.ArrayEnd:
            default:
                throw new UnreachableException();
        }
    }

    internal void WriteArgument(ref OscWriter writer, OscWriterOptions options, bool inArray)
    {
        bool ventuzArray = inArray && options.ArrayEncoding == OscArrayEncoding.VentuzRemoting;
        switch (Tag)
        {
            case OscTypeTag.Int32:
            case OscTypeTag.Float32:
            case OscTypeTag.Char:
            case OscTypeTag.Color:
            case OscTypeTag.Midi:
                writer.WriteInt32((int)_bits);
                break;
            case OscTypeTag.Int64:
            case OscTypeTag.TimeTag:
            case OscTypeTag.Double:
                writer.WriteInt64(_bits);
                break;
            case OscTypeTag.True:
            case OscTypeTag.False:
            case OscTypeTag.Infinitum:
                break;
            case OscTypeTag.Nil:
                if (ventuzArray)
                {
                    writer.WriteString(VentuzNullString);
                }
                break;
            case OscTypeTag.String:
            case OscTypeTag.Symbol:
                {
                    string text = (string)_reference!;
                    _ = StringByteCount(text, options, ventuzArray);
                    if (ventuzArray && text.StartsWith('\\'))
                    {
                        writer.WriteByte((byte)'\\');
                    }
                    writer.WriteString(text);
                    break;
                }
            case OscTypeTag.Blob:
                writer.WriteBlob(BlobMemory.Span);
                break;
            case OscTypeTag.BitVector:
                {
                    bool[] bits = BitVectorBits;
                    int length = OscBitVector.EncodedLength(bits.Length);
                    writer.WriteInt32(length);
                    OscBitVector.Encode(bits, writer.Reserve(OscBinary.Align(length)));
                    break;
                }
            case OscTypeTag.ArrayBegin:
                {
                    OscValue[] elements = ArrayElements;
                    if (options.ArrayEncoding == OscArrayEncoding.VentuzRemoting)
                    {
                        OscTypeTag elementTag = RequireVentuzElementTag(inArray);
                        int byteCount = 0;
                        foreach (OscValue element in elements)
                        {
                            if (VentuzElementTag(element) != elementTag)
                            {
                                throw new OscFormatException("Every element of a Ventuz array must have the same type.");
                            }
                            byteCount += element.GetByteCount(options, inArray: true);
                        }
                        writer.WriteInt32(byteCount);
                        writer.WriteInt32(elements.Length);
                    }
                    foreach (OscValue element in elements)
                    {
                        element.WriteArgument(ref writer, options, inArray: true);
                    }
                    break;
                }
            case OscTypeTag.ArrayEnd:
            default:
                throw new UnreachableException();
        }
    }

    private OscTypeTag RequireVentuzElementTag(bool inArray)
    {
        if (inArray)
        {
            throw new OscFormatException("Ventuz arrays cannot nest.");
        }
        OscTypeTag tag = ArrayElementTag ?? throw new OscFormatException("An empty Ventuz array needs an explicit element tag; create it with FromArray(elements, elementTag).");
        if (tag is OscTypeTag.ArrayBegin or OscTypeTag.ArrayEnd or OscTypeTag.Nil)
        {
            throw new OscFormatException($"A Ventuz array cannot have element type '{(char)tag}'.");
        }
        return tag;
    }

    private static int StringByteCount(string text, OscWriterOptions options, bool ventuzArray)
    {
        if (options.AsciiOnly && !Ascii.IsValid(text))
        {
            throw new OscFormatException("The string contains a character outside ASCII and the writer is restricted to ASCII.");
        }
        int count = Encoding.UTF8.GetByteCount(text);
        return ventuzArray && text.StartsWith('\\') ? count + 1 : count;
    }

    // ----- Storage helpers -----

    private static long Pack(int offset, int length) => ((long)offset << 32) | (uint)length;
    private int BlobOffset => (int)(_bits >> 32);
    private int BlobLength => (int)_bits;

    private ReadOnlyMemory<byte> BlobMemory => _reference switch
    {
        byte[] array => new ReadOnlyMemory<byte>(array, BlobOffset, BlobLength),
        MemoryManager<byte> manager => manager.Memory.Slice(BlobOffset, BlobLength),
        _ => ReadOnlyMemory<byte>.Empty,
    };

    private OscValue[] ArrayElements => _reference as OscValue[] ?? [];
    private bool[] BitVectorBits => _reference as bool[] ?? [];

    private bool TryGetBits(OscTypeTag tag, out int value)
    {
        value = (int)_bits;
        return Tag == tag;
    }

    private bool TryGetBits(OscTypeTag tag, out long value)
    {
        value = _bits;
        return Tag == tag;
    }

    private static string ValidateString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Contains('\0') ? throw new ArgumentException("An OSC string cannot contain a null character.", nameof(value)) : value;
    }

    private InvalidOperationException Mismatch(OscTypeTag expected)
        => new($"The value is of type '{(char)Tag}', not '{(char)expected}'.");
}
