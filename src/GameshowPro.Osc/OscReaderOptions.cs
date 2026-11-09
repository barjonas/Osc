namespace GameshowPro.Osc;

/// <summary>Limits and dialect settings applied while decoding packets.</summary>
public sealed record OscReaderOptions
{
    public static OscReaderOptions Default { get; } = new();

    /// <summary>Array layout expected from the peer. See <see cref="OscArrayEncoding"/>.</summary>
    public OscArrayEncoding ArrayEncoding { get; init; }

    /// <summary>
    /// Whether a type tag this library does not know makes the packet malformed (<see cref="OscUnknownTypeTagHandling.Reject"/>,
    /// the default) or ends argument decoding and keeps the rest of the message as an opaque tail
    /// (<see cref="OscUnknownTypeTagHandling.AcceptOpaque"/>).
    /// </summary>
    public OscUnknownTypeTagHandling UnknownTypeTags { get; init; }

    /// <summary>
    /// Deepest bundle nesting accepted. A bare message or a bundle of messages is depth 1. Defaults to 8.
    /// </summary>
    public int MaxBundleDepth
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxBundleDepth), value, "Must be positive.");
    } = 8;

    /// <summary>Most messages and bundles accepted in one packet, counted across every nesting level. Defaults to 1024.</summary>
    public int MaxBundleElements
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxBundleElements), value, "Must be positive.");
    } = 1024;
}
