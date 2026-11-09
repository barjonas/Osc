namespace GameshowPro.Osc;

/// <summary>Wire-format constants and arithmetic shared by the reader, writer and model.</summary>
internal static class OscBinary
{
    /// <summary>The eight bytes that start every bundle: <c>#bundle</c> and its terminator.</summary>
    internal static ReadOnlySpan<byte> BundlePrefix => "#bundle\0"u8;

    /// <summary>A bundle header: the prefix and a 64-bit time tag.</summary>
    internal const int BundleHeaderLength = 16;

    /// <summary>The shortest valid message: an address of one character and an empty type tag string.</summary>
    internal const int MinimumMessageLength = 8;

    /// <summary>Rounds <paramref name="length"/> up to the next multiple of four.</summary>
    internal static int Align(int length) => (length + 3) & ~3;

    /// <summary>Bytes a string of <paramref name="utf8Length"/> bytes occupies with its terminator and padding.</summary>
    internal static int PaddedStringLength(int utf8Length) => Align(utf8Length + 1);
}
