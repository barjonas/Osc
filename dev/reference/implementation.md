# Implementation reference

Design decisions, their rationale and open items. User documentation is under [`docs/`](../../docs).

## Origin

Created 2026-11-08 to replace the receive-only `OscMessage`/`OscPacket` classes inside the Soundling service with a
shared codec that every GameshowPro application can use for sending, receiving and testing, and that the Soundling
client library can expose to users who prefer raw OSC over the first-class API. It was placed in its own repository,
not in `GameshowPro.Common`, so that it has no dependencies, no Windows/cross-platform dual package, and can be
referenced by AOT and WebAssembly clients without pulling in the rest of Common.

## Encoding design

**Pre-sized single pass.** An OSC packet's size is computable from its content, including nested bundles, so every
`OscPacket` implements `GetByteCount()` and `TryWrite(Span<byte>, out int)` (the BCL `TryFormat` pattern). There is
no buffering writer and no two-pass encode. A bundle writes each element by reserving its four size bytes, letting the
element write itself, then back-filling the size from the writer's position, so nothing is measured twice.
`Write(IBufferWriter<byte>)` and `ToArray()` are built on the same path.

**`OscValue` layout.** 16 bytes: `long _bits`, `object? _reference`, `byte _tag`. Numeric kinds store their bit
pattern in `_bits`; strings hold the `string`; blobs hold the backing `byte[]` or `MemoryManager<byte>` with offset and
length packed into `_bits` (zero-copy from `ReadOnlyMemory<byte>`); arrays hold the `OscValue[]` behind an
`ImmutableArray` via `ImmutableCollectionsMarshal`, with the Ventuz Remoting element-tag hint in `_bits`; bit vectors
hold a `bool[]`. A tag byte of 0 reads as `Nil`, so `default(OscValue)` is nil. Equality is wire equality (bit
patterns), which keeps it cheap and makes "would these encode identically" the definition.

**`OscTypeTag` values are the ASCII tag characters**, so the type tag string is emitted and parsed without a lookup
table. `CA1720` is suppressed on that enum because the members intentionally carry the specification's type names.

**Reader.** `OscReader` is a forward-only `ref struct` over `ReadOnlySpan<byte>`; every `TryRead` returns false and
records the first `OscParseError`. It optionally carries the `ReadOnlyMemory<byte>` the span came from so blob values
can slice it instead of copying. Strings are validated with `Utf8.IsValid` and decoded only when a model is built.
`OscPacket.TryParseCore` recurses per bundle element with a shared element counter and depth, applying
`OscReaderOptions` limits.

**Strings are UTF-8 both ways.** OSC 1.0 says ASCII; Ventuz Remoting and most peers send UTF-8, which is a superset.
`OscWriterOptions.AsciiOnly` exists for strict peers, and `OscWriterOptions.VentuzNode` turns it on because the Ventuz
node library decodes with `Encoding.ASCII`.

**Address validation is permissive**: leading `/`, no control characters. Pattern characters are legal because a
sender may use them. Pattern *matching* is out of scope here.

**Collection-expression overloads.** `OscValue.FromArray` and `FromBitVector` take only `ImmutableArray`, because a
`params ReadOnlySpan` sibling made `FromArray([...])` ambiguous. `OscMessage`/`OscBundle` keep `params` constructors
for the common `new OscMessage("/x", 1, "two")` shape; a collection expression passed to them can be ambiguous, so
assign it to a typed local first.

## Ventuz

Two dialects, established from different sources:

### OSC scene nodes: `Ventuz.OSC.dll` (verified from the Ventuz 8 assembly, 2026-11-08)

Decompiled `C:\Program Files\Ventuz Technology Group\Ventuz 8\Ventuz.OSC.dll` (v8, 2025-08-27 build) with ilspycmd.
Observed in `OscElement`, `OscMessage.OscBinaryReader/Writer`, `OscUnicodeString`, `OscImage`, `UdpReader`,
`UdpWriter`:

- Type map (`SupportedTypes`/`TypeTags`): `int`→`i`, `float`→`f` (`PositiveInfinity`→`I`), `string`→`s`,
  `byte[]`→`b`, `long`→`h`, **`ulong`→`t`**, `double`→`d`, `OscSymbol`→`S`, `char`→`c`, `Color`→`r`,
  `OscMidiMessage`→**`M`** (written upper case; its own reader only accepts `m`), `bool`→`T`/`F`, `null`→`N`.
  `[`/`]` throw "OSC arrays not yet supported". `B` is only an internal placeholder for `bool`; a `B` argument from
  the wire is "unknown type tag".
- **Strings are `Encoding.ASCII`** in both directions. The reader walks four bytes at a time and stops at the first
  word containing a zero, so a standard padded string decodes correctly.
- **Colour** is `Color.ToArgb()` written as a big-endian int: bytes A, R, G, B. The specification's `r` is R, G, B, A.
  `OscVentuz.Color`/`TryGetColor` swap; `OscColor.ToArgb`/`FromArgb` are the primitives.
- **`OscUnicodeString`**: blob of `"BEUC"` + `Encoding.BigEndianUnicode` bytes. **`OscImage`**: blob of `"PNGI"` + a
  PNG saved by `System.Drawing`. Their reader sniffs every `b` for these markers. `OscVentuz` reproduces both without
  a `System.Drawing` dependency (image bytes only).
- Bundles are standard: `#bundle`, 64-bit time tag, int32 size prefix per element, nested bundles allowed.
  `OscBundle.Immediate = 1`. Time conversion uses the 1900 epoch and `ticks % 10^7 * 2^32 / 10^7`, as here.
- **Fragmentation**: `UdpWriter` sends a message over 8160 bytes as `#pundle\0` parts: 8-byte tag, 16-byte GUID,
  int32 total length, int32 offset, chunk (max 8160). `OscPartialMessage` reassembles, expiring partial sets after
  50 s. Not implemented here; `OscVentuz.MaxUnfragmentedDatagramLength` documents the threshold.
- Sockets: receiver binds `0.0.0.0`, `SO_RCVBUF` 512 KiB, `SO_REUSEADDR` when asked, joins multicast by interface
  index; sender sets `IP_MULTICAST_TTL` 10 and `IP_MULTICAST_IF` via `IPAddress.NetworkToHostOrder(index)` (the same
  byte swap this library's sender uses).

### Remoting 4 (TCP; from documentation, not yet captured)

Sources: Ventuz Remoting 4 protocol documentation (V4 and V7 pages, read 2026-11-08).

- `s`/`S` always UTF-8; `B` BitVector; `[ ]` arrays with a single element tag and int32 byte-count/element-count
  header; `\` null string and doubled leading backslash inside arrays; no nested arrays or `N` in arrays; TCP with
  int32 length prefix; images as `b` blobs; GUIDs as 16-byte blobs.
- **Interpretation of the array header.** The documentation says "an int32 byte count, an int32 element count, then
  the element data" and "arrays with zero elements have a byte count of 0". We write the byte count as the length of
  the element data after the two counts and ignore it on read, bounding the read by the element count and per-element
  decoding instead. **Unverified against a capture.** If a Remoting capture shows the byte count including the 8-byte
  header, change `OscValue.WriteArgument` (one line) and add the capture as a test vector.
- **BitVector encoding** was derived from the documentation's examples: bits LSB-first, then one sentinel 1 bit, then
  zero padding; `size = (nBits >> 3) + 1`. Examples reproduced in `OscVentuzDialectTests`: empty → `01`, four true →
  `1F`, eight true → `FF 01` (or `FF 00`), nine true → `FF 03`. A last byte of `00` is accepted as "no bits".
- The element-tag hint on an array value is needed only to encode an empty Remoting array; it is ignored by equality.

## Stream transport

`OscStreamConnection` wraps a duplex `Stream` with a `PipeReader` (from the shared framework's `System.IO.Pipelines`;
no package) for receiving and a reusable `ArrayBufferWriter<byte>` under a `SemaphoreSlim` for sending, so sends are
serialized and allocation settles after the first frame. Frames are pulled with the `ReadOnlySequence<byte>`
overloads of `OscStreamFramer`, so a frame spanning pipe segments is never flattened for length-prefixed framing; SLIP
decodes byte by byte through a `SequenceReader<byte>` into the connection's scratch writer. `MaxPacketLength` is
enforced on the length prefix before buffering, and on buffered-but-unterminated SLIP data, so a hostile peer cannot
make the connection buffer without bound. A clean close between frames ends `Receive` with null; bytes left over at
close are `OscFormatException(UnexpectedEnd)`. `OscPacket.Parse(ReadOnlySequence<byte>)` reads a single segment in
place and copies a multi-segment sequence once through the array pool.

## UDP transport

`OscUdpSender`/`OscUdpReceiver` wrap `Socket` directly and expose .NET's own types for interface selection
(`IPEndPoint`, `NetworkInterface`, `MulticastOption`, `IPv6MulticastOption`). Receive-side behaviour carried over from
Soundling's `OscListenerManager`:

- bind `IPAddress.Any`, enable `IP_PKTINFO` (`SocketOptionName.PacketInformation`) and surface destination address
  and interface index per datagram so the host can attribute traffic and refuse ambiguous ingress;
- `SO_REUSEADDR`, `SO_BROADCAST`, `IP_MULTICAST_LOOP` on (Winsock applies loopback on the receiver, POSIX on the
  sender, so both classes set it);
- count malformed datagrams rather than throwing; swallow Winsock `ConnectionReset` (ICMP port unreachable); treat
  `MessageSize` as malformed;
- decoding limits per datagram.

Left to the host on purpose: network-change reconciliation and retry, per-listener attribution rules, routing,
queueing and back-pressure, OSCQuery and mDNS advertisement. Soundling keeps those in its service.

Sender details: `IP_MULTICAST_IF` is set from the `NetworkInterface` index in network byte order (as `UdpClient`
does) or, when only a bound IPv4 address is given, from that address as a 4-byte `in_addr`.

## Testing

MSTest, 68 tests as of 2026-11-08: the two OSC 1.0 specification examples byte for byte, round trips of every type,
UTF-8 and ASCII-only, `TryWrite` sizing, streaming writer parity, raw reader access, bundles including the Ventuz
capture from Soundling (`/sound/strike/play` in an immediate bundle), nested bundles, malformed inputs for each
`OscParseError`, depth and element limits, Remoting arrays, escapes and bit vectors, node-library `BEUC`/`PNGI`
blobs, ARGB colours and the `M` tag, time tag conversions, stream framing over spans and multi-segment sequences,
`OscStreamConnection` over loopback TCP in both framings (coalesced and split writes, clean close, truncated frame,
oversized prefix), and loopback UDP with packet information, malformed counting, cancellation and per-call
destinations.

**Soundling validation (2026-11-08).** The Soundling service was migrated onto this library through a local
`ProjectReference` (its `OscMessage.cs`/`OscPacket.cs` deleted; `OscDatagram.cs` holds its limits and the
scheduled-bundle rejection; `OscListenerManager.PortSocket` now wraps `OscUdpReceiver`; `OscOverloads` dispatches on
`OscMessage.TypeTags`). The whole Soundling solution built and 1132 tests passed, including the live loopback and
multicast runtime tests. The migration surfaced three additions, all made: `OscPacket.EnumerateMessages()`,
`OscBundle.IsImmediate`, and element counting that excludes the outermost packet so Soundling's documented
"32 elements" limit maps to `MaxBundleElements = 32`.

## Open items

- Capture a Remoting 4 session to confirm the array byte-count interpretation and the `[t]` single-element-tag
  reading; add the capture as a test vector.
- Decide whether to reassemble Ventuz `#pundle` fragments (needed only to receive images or very large messages from
  a node). The format is fully known from the assembly; it would be a small stateful helper above `OscUdpReceiver`.
- CI runs on Windows (mirroring the Common repository). A Linux job would exercise the UDP packet-information and
  multicast paths on the platform Soundling deploys to.
- No multicast or broadcast test exists; they need an interface and are environment dependent. The loopback test
  checks the interface index against `NetworkInterface` for the loopback adapter.
- Benchmarks (BenchmarkDotNet) were not added; the design is allocation-conscious but unmeasured.
- Soundling: once the first package is published, replace the Service's temporary `ProjectReference` to
  `..\..\..\Osc\src\GameshowPro.Osc` with a `PackageReference`, then expose `OscMessage`/`OscBundle` from
  `GameshowPro.Soundling.Client` for users who prefer raw OSC.
