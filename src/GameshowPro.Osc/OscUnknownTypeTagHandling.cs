namespace GameshowPro.Osc;

/// <summary>
/// What the reader does when a message's type tag string contains a character this library does not know.
/// See <see cref="OscReaderOptions.UnknownTypeTags"/>.
/// </summary>
public enum OscUnknownTypeTagHandling
{
    /// <summary>The packet is malformed: parsing fails with <see cref="OscParseError.UnsupportedTypeTag"/>. The default.</summary>
    Reject,
    /// <summary>
    /// Decoding stops at the first unknown top-level tag. The arguments before it are decoded as usual; that tag,
    /// every tag after it and every remaining byte of the message are kept verbatim as
    /// <see cref="OscMessage.OpaqueTypeTags"/> and <see cref="OscMessage.OpaqueArguments"/>, so the message still
    /// round-trips byte for byte. An unknown tag inside an array is still rejected, because the array's end cannot
    /// be found without knowing the size of every element.
    /// </summary>
    AcceptOpaque,
}
