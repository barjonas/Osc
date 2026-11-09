# Ventuz

[Ventuz](https://www.ventuz.com/) speaks OSC in two places, and they are **different dialects**:

- **OSC scene nodes** (the OSC input/output nodes in a scene, UDP) use `Ventuz.OSC.dll`, Ventuz's own OSC library.
- **Remoting 4** (TCP) is a separate implementation documented in the
  [Remoting 4 protocol](https://www.ventuz.com/support/help/latest/Remoting4Protocol.html) pages.

The facts below about the nodes come from the shipped Ventuz 8 `Ventuz.OSC.dll` (inspected 2026-11-08); the
Remoting facts come from its documentation.

## OSC scene nodes (`Ventuz.OSC.dll`, UDP)

Use `OscWriterOptions.VentuzNode` when sending to a node. Everything the nodes do rides on standard types, so
nothing special is needed to receive from them.

| Node behaviour | What to do |
|---|---|
| `s` and `S` strings are **ASCII**. A UTF-8 byte above 127 decodes as `?` on the Ventuz side. | `OscWriterOptions.VentuzNode` sets `AsciiOnly`, so non-ASCII text throws instead of being mangled. Send Unicode as below. |
| Unicode text travels as a **blob** of `BEUC` + UTF-16 big-endian bytes (`OscUnicodeString`). | `OscVentuz.UnicodeString("…")` to send; `OscVentuz.TryGetUnicodeString(value, out text)` to read. Other peers see an ordinary blob. |
| Images travel as a **blob** of `PNGI` + a PNG file (`OscImage`). There is no image type tag. | `OscVentuz.Image(pngBytes)` / `OscVentuz.TryGetImage(value, out png)`. |
| Colours (`r`) are written as `Color.ToArgb()`: bytes **A R G B**, not the RGBA of the specification. | `OscVentuz.Color(oscColor)` to send; `OscVentuz.TryGetColor(value, out color)` to read. |
| MIDI messages are written with tag `M` (upper case); the standard tag is `m`. | Nothing. The reader accepts both; this library writes `m`. |
| `ulong` arguments are sent as `t`. | Use an `OscTimeTag`, which is the same 64-bit word. |
| Arrays (`[` `]`) are rejected ("OSC arrays not yet supported"); `B` is unknown. | Do not send arrays or bit vectors to a node. |
| `bool` → `T`/`F`, `null` → `N`, `float.PositiveInfinity` → `I`, `long` → `h`, `double` → `d`, `char` → `c`, `OscSymbol` → `S`. | Standard. |
| Datagrams over 8160 bytes are split into proprietary `#pundle` parts (8-byte tag, 16-byte GUID, int32 total length, int32 offset, chunk) and reassembled by the receiver within 50 s. | Keep datagrams under `OscVentuz.MaxUnfragmentedDatagramLength` (8160). This library does not reassemble `#pundle` parts; a large image from a node arrives as several unparseable datagrams. |
| The node receiver binds `0.0.0.0`, joins a multicast group on a chosen interface index, and reads with a 512 KiB socket buffer. The sender sets multicast TTL 10 and `IP_MULTICAST_IF` by interface index. | Matches what `OscUdpSender`/`OscUdpReceiver` do. |
| Bundles are standard, including nested bundles and element size prefixes. | Nothing. |

## Remoting 4 (TCP)

Use `OscWriterOptions.VentuzRemoting` and `new OscReaderOptions { ArrayEncoding = OscArrayEncoding.VentuzRemoting }`.

| Remoting behaviour | What to do |
|---|---|
| Strings (`s`) and symbols (`S`) are always UTF-8. | Nothing. This library always encodes and decodes UTF-8. |
| Bit vectors use type tag `B`: booleans packed LSB-first into a blob with a sentinel bit. | Nothing. `B` is a first-class type: `OscValue.FromBitVector([...])`, `AsBitVector()`. |
| Arrays are typed once, not per element: the tags are `[`, one element tag, `]` and the data is an int32 byte count, an int32 element count, then the elements. The tag characters are the same as OSC 1.1 arrays, so a reader cannot tell the two apart. | Set `ArrayEncoding = OscArrayEncoding.VentuzRemoting` on both writer and reader options. Elements must share one type and arrays cannot nest; an empty array needs `OscValue.FromArray([], elementTag)`. |
| A null string inside an array is sent as `\`; a string that starts with `\` has it doubled. Arrays cannot contain `N`. | Nothing beyond the array encoding. `OscValue.Nil` inside an array becomes `\` on the wire and `\` decodes to `OscValue.Nil`. |
| TCP with a big-endian int32 length before each packet. | `OscStreamFramer` with `OscStreamFraming.LengthPrefixed`. |
| Snapshot images come back as a blob (`b`) holding a BMP, PNG, TIF or JPG file. | `AsBlob()` gives the file bytes. |
| GUIDs travel as 16-byte blobs. | `new Guid(value.AsBlob().Span)` and `OscValue.FromBlob(guid.ToByteArray())`. |
| Addresses may contain any character except `/`; wildcards are not supported. | Nothing. Address validation here only forbids control characters. |

## What the library does not know

- Which Ventuz commands and node addresses exist, and their argument lists. Those belong to the application.
- The Remoting array byte count is written as the length of the element data after the two counts and is not
  checked on read. This matches the documentation's statement that an empty array has a byte count of 0, but
  Remoting itself has not been captured. See the [implementation notes](../dev/reference/implementation.md).
