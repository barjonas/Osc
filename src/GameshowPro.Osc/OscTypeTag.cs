namespace GameshowPro.Osc;

/// <summary>
/// OSC argument type tags. Each member's value is the ASCII code of its tag character, so a type tag
/// string is emitted and parsed without any translation table.
/// </summary>
/// <remarks>
/// <c>i f s b</c> are the OSC 1.0 required types; <c>h t d S c r m T F N I [ ]</c> are the OSC 1.1
/// optional types. <see cref="BitVector"/> (<c>B</c>) is the Ventuz extension: a packed bit array
/// carried in blob framing. Strings and symbols are always UTF-8 on the wire, which is a strict
/// superset of the ASCII the OSC 1.0 specification describes and matches Ventuz and most other peers.
/// </remarks>
// CA1720: these members deliberately carry the OSC specification's own type names (int32, float32, string,
// int64, double, char), which readers of the protocol expect; renaming them would obscure the mapping.
#pragma warning disable CA1720
public enum OscTypeTag : byte
{
    /// <summary>32-bit big-endian two's complement integer (<c>i</c>).</summary>
    Int32 = (byte)'i',
    /// <summary>32-bit big-endian IEEE 754 float (<c>f</c>).</summary>
    Float32 = (byte)'f',
    /// <summary>Null-terminated UTF-8 string padded to four bytes (<c>s</c>).</summary>
    String = (byte)'s',
    /// <summary>Int32 byte count followed by the bytes, padded to four bytes (<c>b</c>).</summary>
    Blob = (byte)'b',
    /// <summary>64-bit big-endian two's complement integer (<c>h</c>).</summary>
    Int64 = (byte)'h',
    /// <summary>64-bit NTP time tag (<c>t</c>); see <see cref="OscTimeTag"/>.</summary>
    TimeTag = (byte)'t',
    /// <summary>64-bit big-endian IEEE 754 double (<c>d</c>).</summary>
    Double = (byte)'d',
    /// <summary>Alternate string type tag (<c>S</c>), encoded exactly like <see cref="String"/>.</summary>
    Symbol = (byte)'S',
    /// <summary>A single ASCII character carried in 32 bits (<c>c</c>).</summary>
    Char = (byte)'c',
    /// <summary>32-bit RGBA colour (<c>r</c>); see <see cref="OscColor"/>.</summary>
    Color = (byte)'r',
    /// <summary>4-byte MIDI message (<c>m</c>); see <see cref="OscMidiMessage"/>.</summary>
    Midi = (byte)'m',
    /// <summary>Boolean true, no argument bytes (<c>T</c>).</summary>
    True = (byte)'T',
    /// <summary>Boolean false, no argument bytes (<c>F</c>).</summary>
    False = (byte)'F',
    /// <summary>Nil, no argument bytes (<c>N</c>).</summary>
    Nil = (byte)'N',
    /// <summary>Infinitum (impulse), no argument bytes (<c>I</c>).</summary>
    Infinitum = (byte)'I',
    /// <summary>Start of an array (<c>[</c>). An <see cref="OscValue"/> holding an array carries this tag.</summary>
    ArrayBegin = (byte)'[',
    /// <summary>End of an array (<c>]</c>). Never the tag of a value.</summary>
    ArrayEnd = (byte)']',
    /// <summary>Ventuz bit vector (<c>B</c>): booleans packed LSB-first into a blob with a sentinel bit.</summary>
    BitVector = (byte)'B',
}
#pragma warning restore CA1720
