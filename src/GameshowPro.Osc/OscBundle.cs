namespace GameshowPro.Osc;

/// <summary>A time tag and an ordered list of elements, each a message or another bundle. Immutable; equality is structural.</summary>
public sealed class OscBundle : OscPacket, IEquatable<OscBundle>
{
    public OscBundle(OscTimeTag timeTag, ImmutableArray<OscPacket> elements)
    {
        TimeTag = timeTag;
        Elements = elements.IsDefault ? [] : elements;
        if (Elements.Any(element => element is null))
        {
            throw new ArgumentException("A bundle cannot contain a null element.", nameof(elements));
        }
    }

    public OscBundle(OscTimeTag timeTag, params ReadOnlySpan<OscPacket> elements) : this(timeTag, ImmutableArray.Create(elements))
    {
    }

    /// <summary>A bundle to be acted on as soon as it arrives.</summary>
    public OscBundle(params ReadOnlySpan<OscPacket> elements) : this(OscTimeTag.Immediate, elements)
    {
    }

    public OscTimeTag TimeTag { get; }

    public ImmutableArray<OscPacket> Elements { get; }

    /// <summary>Every message in this bundle and in nested bundles, in wire order.</summary>
    public override IEnumerable<OscMessage> EnumerateMessages()
    {
        foreach (OscPacket element in Elements)
        {
            foreach (OscMessage message in element.EnumerateMessages())
            {
                yield return message;
            }
        }
    }

    /// <summary>True when this bundle and every nested bundle carry <see cref="OscTimeTag.Immediate"/>.</summary>
    public bool IsImmediate => TimeTag.IsImmediate && Elements.All(element => element is not OscBundle nested || nested.IsImmediate);

    public override int GetByteCount(OscWriterOptions? options = null)
    {
        OscWriterOptions resolved = options ?? OscWriterOptions.Default;
        int total = OscBinary.BundleHeaderLength;
        foreach (OscPacket element in Elements)
        {
            total += 4 + element.GetByteCount(resolved);
        }
        return total;
    }

    internal override void WriteCore(ref OscWriter writer, OscWriterOptions options)
    {
        writer.WriteBundleHeader(TimeTag);
        foreach (OscPacket element in Elements)
        {
            // Reserve the size prefix and fill it in after the element has written itself, so nothing is measured twice.
            Span<byte> size = writer.Reserve(4);
            int start = writer.BytesWritten;
            element.WriteCore(ref writer, options);
            BinaryPrimitives.WriteInt32BigEndian(size, writer.BytesWritten - start);
        }
    }

    /// <summary>Decodes a bundle, throwing <see cref="OscFormatException"/> when the data is malformed or is a bare message.</summary>
    public static new OscBundle Parse(ReadOnlySpan<byte> data, OscReaderOptions? options = null)
        => OscPacket.Parse(data, options) as OscBundle ?? throw new OscFormatException("The packet is a message, not a bundle.");

    public static bool TryParse(ReadOnlySpan<byte> data, [NotNullWhen(true)] out OscBundle? bundle, out OscParseError error, OscReaderOptions? options = null)
    {
        bundle = TryParse(data, out OscPacket? packet, out error, options) ? packet as OscBundle : null;
        return bundle is not null;
    }

    public bool Equals(OscBundle? other)
        => other is not null && TimeTag == other.TimeTag && Elements.SequenceEqual(other.Elements);

    public override bool Equals(object? obj) => Equals(obj as OscBundle);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(TimeTag);
        foreach (OscPacket element in Elements)
        {
            hash.Add(element);
        }
        return hash.ToHashCode();
    }

    public override string ToString() => $"#bundle {TimeTag} [{string.Join("; ", Elements)}]";
}
