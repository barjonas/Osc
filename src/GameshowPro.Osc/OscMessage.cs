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

    /// <summary>
    /// A message whose type tag string ended in tags the reader did not know, decoded under
    /// <see cref="OscUnknownTypeTagHandling.AcceptOpaque"/>: the decoded arguments, then the unknown tags and the
    /// bytes they describe, kept verbatim so the message still encodes byte for byte.
    /// </summary>
    internal OscMessage(string address, ImmutableArray<OscValue> arguments, string opaqueTypeTags, ReadOnlyMemory<byte> opaqueArguments)
        : this(address, arguments)
    {
        Debug.Assert(opaqueTypeTags.Length > 0 && Ascii.IsValid(opaqueTypeTags), "Opaque type tags must be ASCII tag characters from the wire.");
        Debug.Assert(opaqueArguments.Length % 4 == 0, "An opaque tail must be a multiple of four bytes.");
        OpaqueTypeTags = opaqueTypeTags;
        OpaqueArguments = opaqueArguments;
    }

    public string Address { get; }

    /// <summary>The decoded arguments. When <see cref="OpaqueTypeTags"/> is set these are only the arguments before the first unknown tag.</summary>
    public ImmutableArray<OscValue> Arguments { get; }

    /// <summary>
    /// The type tag characters from the first unknown tag to the end of the type tag string, when the message was
    /// decoded under <see cref="OscUnknownTypeTagHandling.AcceptOpaque"/> and met one; null for an ordinary message.
    /// Appending these to the tags of <see cref="Arguments"/> reproduces the full <see cref="TypeTags"/>.
    /// </summary>
    public string? OpaqueTypeTags { get; }

    /// <summary>
    /// Every byte of the message after the last decoded argument: the data described by <see cref="OpaqueTypeTags"/>.
    /// Empty for an ordinary message. When the message was parsed from a <see cref="ReadOnlyMemory{T}"/> this
    /// references the source buffer in place, exactly as a blob argument does; when parsed from a span it is a copy.
    /// </summary>
    public ReadOnlyMemory<byte> OpaqueArguments { get; }

    /// <summary>
    /// The type tag string in the standard dialect, including the leading comma, for example <c>",ifs"</c>, and
    /// including <see cref="OpaqueTypeTags"/> when the message carries an opaque tail.
    /// </summary>
    public string TypeTags => field ??= BuildTypeTags();

    public override IEnumerable<OscMessage> EnumerateMessages() => [this];

    public override int GetByteCount(OscWriterOptions? options = null)
    {
        OscWriterOptions resolved = options ?? OscWriterOptions.Default;
        int tags = 1 + (OpaqueTypeTags?.Length ?? 0);
        int arguments = OpaqueArguments.Length;
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
        if (OpaqueTypeTags is { } opaqueTags)
        {
            foreach (char tag in opaqueTags)
            {
                writer.WriteByte((byte)tag);
            }
        }
        writer.WriteUtf8String([]);
        foreach (OscValue value in Arguments)
        {
            value.WriteArgument(ref writer, options, inArray: false);
        }
        OpaqueArguments.Span.CopyTo(writer.Reserve(OpaqueArguments.Length));
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
        => other is not null
            && string.Equals(Address, other.Address, StringComparison.Ordinal)
            && Arguments.AsSpan().SequenceEqual(other.Arguments.AsSpan())
            && string.Equals(OpaqueTypeTags, other.OpaqueTypeTags, StringComparison.Ordinal)
            && OpaqueArguments.Span.SequenceEqual(other.OpaqueArguments.Span);

    public override bool Equals(object? obj) => Equals(obj as OscMessage);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Address, StringComparer.Ordinal);
        foreach (OscValue value in Arguments)
        {
            hash.Add(value);
        }
        hash.Add(OpaqueTypeTags, StringComparer.Ordinal);
        hash.AddBytes(OpaqueArguments.Span);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        if (Arguments.IsEmpty && OpaqueTypeTags is null)
        {
            return Address;
        }
        string[] parts = [Address, TypeTags, string.Join(' ', Arguments), OpaqueTypeTags is null ? "" : $"[{OpaqueArguments.Length} opaque bytes]"];
        return string.Join(' ', parts.Where(part => part.Length > 0));
    }

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
        return Encoding.ASCII.GetString(tags) + OpaqueTypeTags;
    }
}
