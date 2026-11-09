namespace GameshowPro.Osc;

/// <summary>
/// How the contents of an array (<c>[</c> ... <c>]</c>) are laid out. The type tag characters are the same in
/// both encodings, so a reader cannot detect the layout from the packet and must be told which peer it talks to.
/// </summary>
public enum OscArrayEncoding
{
    /// <summary>OSC 1.1: every element has its own tag between the brackets and the data follows with no counts. Arrays may nest.</summary>
    Standard,

    /// <summary>
    /// Ventuz Remoting 4 (TCP): the array is typed once (<c>[i]</c>), and the data is an int32 byte count of the
    /// element data, an int32 element count, then the elements. Arrays cannot nest or hold <c>N</c>; a null string
    /// element is sent as <c>\</c>, and a string that starts with a backslash has that backslash doubled. This library
    /// decodes <c>\</c> to <see cref="OscValue.Nil"/> and encodes a <see cref="OscValue.Nil"/> element as <c>\</c>.
    /// The Ventuz OSC scene nodes do not support arrays at all; see <see cref="OscVentuz"/>.
    /// </summary>
    VentuzRemoting,
}
