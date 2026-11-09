namespace GameshowPro.Osc;

/// <summary>Dialect settings applied while encoding packets.</summary>
public sealed record OscWriterOptions
{
    public static OscWriterOptions Default { get; } = new();

    /// <summary>Settings for a Ventuz Remoting 4 peer (TCP): typed arrays, UTF-8 strings.</summary>
    public static OscWriterOptions VentuzRemoting { get; } = new() { ArrayEncoding = OscArrayEncoding.VentuzRemoting };

    /// <summary>
    /// Settings for a Ventuz OSC scene node (UDP, the <c>Ventuz.OSC</c> library): strings are ASCII there, so
    /// non-ASCII text is rejected rather than silently mangled. Send Unicode text with
    /// <see cref="OscVentuz.UnicodeString"/> instead. Arrays are not supported by those nodes.
    /// </summary>
    public static OscWriterOptions VentuzNode { get; } = new() { AsciiOnly = true };

    /// <summary>Array layout the peer expects. See <see cref="OscArrayEncoding"/>.</summary>
    public OscArrayEncoding ArrayEncoding { get; init; }

    /// <summary>
    /// When true, a string, symbol or address that contains a character outside 7-bit ASCII is rejected
    /// with <see cref="OscFormatException"/> instead of being encoded as UTF-8. For peers that implement the
    /// letter of OSC 1.0, including the Ventuz OSC scene nodes.
    /// </summary>
    public bool AsciiOnly { get; init; }
}
