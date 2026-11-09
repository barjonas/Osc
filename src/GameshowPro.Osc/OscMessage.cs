namespace GameshowPro.Osc;

/// <summary>An address pattern and its arguments. Immutable; equality is structural.</summary>
public sealed class OscMessage : OscPacket, IEquatable<OscMessage>
{
    public OscMessage(string address, ImmutableArray<OscValue> arguments)
    {
        Address = OscAddress.Validate(address);
        Arguments = arguments.IsDefault ? [] : arguments;
    }

    /// <summary>Builds a message from values that convert implicitly, for example <c>new OscMessage("/cue/1/go", 1, 0.5f, "fast")</c>.</summary>
    public OscMessage(string address, params ReadOnlySpan<OscValue> arguments) : this(address, ImmutableArray.Create(arguments))
    {
    }

    public string Address { get; }

    public ImmutableArray<OscValue> Arguments { get; }

    /// <summary>The type tag string in the standard dialect, including the leading comma, for example <c>",ifs"</c>.</summary>
    public string TypeTags => field ??= BuildTypeTags();

    public override IEnumerable<OscMessage> EnumerateMessages() => [this];

    public override int GetByteCount(OscWriterOptions? options = null)
    {
        OscWriterOptions resolved = options ?? OscWriterOptions.Default;
        int tags = 1;
        int arguments = 0;
        foreach (OscValue value in Arguments)
        {
            tags += value.GetTypeTagCount(resolved);
            arguments += value.GetByteCount(resolved, inArray: false);
        }
        return OscBinary.PaddedStringLength(AddressByteCount(resolved)) + OscBinary.PaddedStringLength(tags) + arguments;
    }

    internal override void WriteCore(ref OscWriter writer, OscWriterOptions options)
    {
        _ = AddressByteCount(options);
        writer.WriteString(Address);
        writer.WriteByte((byte)',');
        foreach (OscValue value in Arguments)
        {
            value.WriteTypeTags(ref writer, options, inArray: false);
        }
        writer.WriteUtf8String([]);
        foreach (OscValue value in Arguments)
        {
            value.WriteArgument(ref writer, options, inArray: false);
        }
    }

    /// <summary>Decodes a message, throwing <see cref="OscFormatException"/> when the data is malformed or is a bundle.</summary>
    public static new OscMessage Parse(ReadOnlySpan<byte> data, OscReaderOptions? options = null)
        => OscPacket.Parse(data, options) as OscMessage ?? throw new OscFormatException("The packet is a bundle, not a message.");

    public static bool TryParse(ReadOnlySpan<byte> data, [NotNullWhen(true)] out OscMessage? message, out OscParseError error, OscReaderOptions? options = null)
    {
        message = TryParse(data, out OscPacket? packet, out error, options) ? packet as OscMessage : null;
        return message is not null;
    }

    public bool Equals(OscMessage? other)
        => other is not null && string.Equals(Address, other.Address, StringComparison.Ordinal) && Arguments.AsSpan().SequenceEqual(other.Arguments.AsSpan());

    public override bool Equals(object? obj) => Equals(obj as OscMessage);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Address, StringComparer.Ordinal);
        foreach (OscValue value in Arguments)
        {
            hash.Add(value);
        }
        return hash.ToHashCode();
    }

    public override string ToString() => Arguments.IsEmpty ? Address : $"{Address} {TypeTags} {string.Join(' ', Arguments)}";

    private int AddressByteCount(OscWriterOptions options)
        => options.AsciiOnly && !Ascii.IsValid(Address)
            ? throw new OscFormatException("The address contains a character outside ASCII and the writer is restricted to ASCII.")
            : Encoding.UTF8.GetByteCount(Address);

    private string BuildTypeTags()
    {
        int count = 1;
        foreach (OscValue value in Arguments)
        {
            count += value.GetTypeTagCount(OscWriterOptions.Default);
        }
        Span<byte> tags = count <= 256 ? stackalloc byte[count] : new byte[count];
        OscWriter writer = new(tags);
        writer.WriteByte((byte)',');
        foreach (OscValue value in Arguments)
        {
            value.WriteTypeTags(ref writer, OscWriterOptions.Default, inArray: false);
        }
        return Encoding.ASCII.GetString(tags);
    }
}
