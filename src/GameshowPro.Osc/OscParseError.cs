namespace GameshowPro.Osc;

/// <summary>Why a packet or part of a packet could not be decoded.</summary>
public enum OscParseError
{
    None,
    /// <summary>The data is empty or shorter than the smallest valid packet.</summary>
    EmptyPacket,
    /// <summary>The packet is not a multiple of four bytes.</summary>
    InvalidLength,
    /// <summary>The address is missing, does not start with <c>/</c>, or contains a control character.</summary>
    InvalidAddress,
    /// <summary>The type tag string is missing or does not start with <c>,</c>.</summary>
    MissingTypeTags,
    /// <summary>A type tag character is not one this library knows.</summary>
    UnsupportedTypeTag,
    /// <summary>Array brackets are unbalanced or nested where the dialect forbids it.</summary>
    InvalidArray,
    /// <summary>A field needs more bytes than remain, including a string whose terminator never arrives.</summary>
    UnexpectedEnd,
    /// <summary>Padding bytes after a string or blob are not zero.</summary>
    InvalidPadding,
    /// <summary>A string is not valid UTF-8.</summary>
    InvalidString,
    /// <summary>A blob length is negative or runs past the end.</summary>
    InvalidBlobLength,
    /// <summary>A bit vector blob is empty or malformed.</summary>
    InvalidBitVector,
    /// <summary>A bundle lacks the <c>#bundle</c> header or a time tag.</summary>
    InvalidBundleHeader,
    /// <summary>A bundle element size is not a positive multiple of four within the bundle.</summary>
    InvalidElementSize,
    /// <summary>Bundles nest deeper than <see cref="OscReaderOptions.MaxBundleDepth"/>.</summary>
    BundleTooDeep,
    /// <summary>A packet carries more elements than <see cref="OscReaderOptions.MaxBundleElements"/>.</summary>
    TooManyElements,
    /// <summary>Bytes remain after the last argument.</summary>
    TrailingData,
}
