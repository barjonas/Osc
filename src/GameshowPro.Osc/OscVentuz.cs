namespace GameshowPro.Osc;

/// <summary>
/// Conventions of <c>Ventuz.OSC.dll</c>, the library behind the OSC input and output nodes in a Ventuz scene
/// (UDP). Everything here rides on standard types, so peers that do not know the convention still receive
/// well-formed OSC: a Unicode string or an image is a blob with a four-byte marker, and a colour is a standard
/// <c>r</c> argument in a different byte order.
/// </summary>
/// <remarks>
/// Established from the shipped Ventuz 8 assembly (2026-11-08). That library encodes <c>s</c> and <c>S</c> as
/// ASCII, maps <c>ulong</c> to <c>t</c>, writes MIDI with tag <c>M</c> (this library reads both <c>m</c> and
/// <c>M</c>), rejects arrays, and splits datagrams over <see cref="MaxUnfragmentedDatagramLength"/> bytes into
/// proprietary <c>#pundle</c> parts that this library does not reassemble. Ventuz Remoting 4 over TCP is a
/// different dialect; see <see cref="OscArrayEncoding.VentuzRemoting"/>.
/// </remarks>
public static class OscVentuz
{
    /// <summary>The four bytes that start a blob carrying a UTF-16 big-endian string.</summary>
    public static ReadOnlySpan<byte> UnicodeStringMarker => "BEUC"u8;

    /// <summary>The four bytes that start a blob carrying a PNG image.</summary>
    public static ReadOnlySpan<byte> ImageMarker => "PNGI"u8;

    /// <summary>Largest datagram the Ventuz OSC node library sends in one piece.</summary>
    public const int MaxUnfragmentedDatagramLength = 8160;

    /// <summary>A Unicode string as the Ventuz nodes understand it: a blob of <c>BEUC</c> followed by UTF-16 big-endian text.</summary>
    public static OscValue UnicodeString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] bytes = new byte[UnicodeStringMarker.Length + Encoding.BigEndianUnicode.GetByteCount(text)];
        UnicodeStringMarker.CopyTo(bytes);
        _ = Encoding.BigEndianUnicode.GetBytes(text, bytes.AsSpan(UnicodeStringMarker.Length));
        return OscValue.FromBlob(bytes);
    }

    public static bool IsUnicodeString(OscValue value) => HasMarker(value, UnicodeStringMarker);

    public static bool TryGetUnicodeString(OscValue value, [NotNullWhen(true)] out string? text)
    {
        if (!HasMarker(value, UnicodeStringMarker))
        {
            text = null;
            return false;
        }
        text = Encoding.BigEndianUnicode.GetString(value.AsBlob().Span[UnicodeStringMarker.Length..]);
        return true;
    }

    /// <summary>An image as the Ventuz nodes understand it: a blob of <c>PNGI</c> followed by a PNG file.</summary>
    public static OscValue Image(ReadOnlySpan<byte> png)
    {
        byte[] bytes = new byte[ImageMarker.Length + png.Length];
        ImageMarker.CopyTo(bytes);
        png.CopyTo(bytes.AsSpan(ImageMarker.Length));
        return OscValue.FromBlob(bytes);
    }

    public static bool IsImage(OscValue value) => HasMarker(value, ImageMarker);

    /// <summary>The PNG file bytes of an image blob, referencing the blob in place.</summary>
    public static bool TryGetImage(OscValue value, out ReadOnlyMemory<byte> png)
    {
        if (!HasMarker(value, ImageMarker))
        {
            png = default;
            return false;
        }
        png = value.AsBlob()[ImageMarker.Length..];
        return true;
    }

    /// <summary>
    /// A colour in the byte order the Ventuz nodes use for <c>r</c>: ARGB (<c>Color.ToArgb()</c>) rather than the
    /// RGBA of the OSC specification.
    /// </summary>
    public static OscValue Color(OscColor color) => OscValue.FromColor(OscColor.FromPacked(unchecked((uint)color.ToArgb())));

    /// <summary>Reads an <c>r</c> argument written by a Ventuz node (ARGB order).</summary>
    public static bool TryGetColor(OscValue value, out OscColor color)
    {
        if (!value.TryGetColor(out OscColor raw))
        {
            color = default;
            return false;
        }
        color = OscColor.FromArgb(unchecked((int)raw.ToPacked()));
        return true;
    }

    private static bool HasMarker(OscValue value, ReadOnlySpan<byte> marker)
        => value.TryGetBlob(out ReadOnlyMemory<byte> blob) && blob.Span.StartsWith(marker);
}
