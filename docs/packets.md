# Packets and values

Everything in this library lives in the `GameshowPro.Osc` namespace.

## Model

`OscPacket` is a closed hierarchy with two members:

- `OscMessage(string address, params ReadOnlySpan<OscValue> arguments)` — an address pattern and its arguments.
  `Address` must start with `/` and contain no control characters; pattern characters (`* ? [ ] { }`) are allowed
  because a sender may address several methods at once. `TypeTags` gives the standard-dialect tag string
  (`",ifs"`).
- `OscBundle(OscTimeTag timeTag, params ReadOnlySpan<OscPacket> elements)` — a time tag and elements that are
  messages or further bundles. `new OscBundle(elements...)` makes an immediate bundle. `EnumerateMessages()` walks
  every message depth-first in wire order.

Both are immutable, compare structurally (`Equals`, `GetHashCode`, `==` on values), and have readable `ToString()`.

### `OscValue`

A 16-byte union over every argument type. Numbers, booleans, nil, infinitum, time tags, characters, colours and MIDI
messages are stored inline; strings, blobs, arrays and bit vectors hold one reference.

| Tag | `OscTypeTag` | Create | Read |
|---|---|---|---|
| `i` | `Int32` | `OscValue.FromInt32(1)` or just `1` | `AsInt32()`, `TryGetInt32(out int)` |
| `f` | `Float32` | `1.5f` | `AsSingle()` |
| `s` | `String` | `"text"` | `AsString()`, `TryGetString(out string?)` |
| `b` | `Blob` | `OscValue.FromBlob(bytes)` or a `byte[]` / `ReadOnlyMemory<byte>` | `AsBlob()` → `ReadOnlyMemory<byte>` |
| `h` | `Int64` | `1L` | `AsInt64()` |
| `t` | `TimeTag` | an `OscTimeTag` | `AsTimeTag()` |
| `d` | `Double` | `1.5` | `AsDouble()` |
| `S` | `Symbol` | `OscValue.FromSymbol("name")` | `AsString()` |
| `c` | `Char` | `'x'` | `AsChar()` |
| `r` | `Color` | an `OscColor(r, g, b, a)` | `AsColor()` |
| `m` | `Midi` | an `OscMidiMessage(port, status, data1, data2)` | `AsMidi()` |
| `T` `F` | `True` `False` | `true` / `false`, `OscValue.True` | `AsBoolean()` |
| `N` | `Nil` | `OscValue.Nil` (also `default`) | `IsNil` |
| `I` | `Infinitum` | `OscValue.Infinitum` | `Tag == OscTypeTag.Infinitum` |
| `[ ]` | `ArrayBegin` | `OscValue.FromArray([1, "x"])` | `AsArray()` → `ImmutableArray<OscValue>` |
| `B` | `BitVector` | `OscValue.FromBitVector([true, false])` | `AsBitVector()` → `ImmutableArray<bool>` |

`TryGetNumber(out double)` reads any of `i h f d`. `ToObject()` boxes the value as its natural CLR type for
reflection-free interop with code that still works on `object`. The reader also accepts `M` as a MIDI tag, which
the Ventuz node library writes; this library always writes `m`.

`OscVentuz` adds the Ventuz node conventions that ride on blobs and colours: `UnicodeString`, `Image` and `Color`,
each with `TryGet…`/`Is…` counterparts. See [Ventuz](ventuz.md).

`FromBlob(ReadOnlyMemory<byte>)` references the caller's memory without copying; do not change the bytes afterwards.

Equality is wire equality: two values are equal when they would encode to the same bytes, so floats compare by bit
pattern (`NaN == NaN`, `0.0 != -0.0`).

### `OscTimeTag`

A 64-bit NTP time: seconds since 1900 in the high word, fraction in the low word. `OscTimeTag.Immediate` (the value
1) means "now". Convert with `FromDateTimeOffset`, `ToDateTimeOffset` and `Now(TimeProvider?)`. The library never
interprets time tags; whether to honour, queue or reject a scheduled bundle is the application's policy.

## Encoding

Every `OscPacket` knows its exact size, so encoding is one pass with no intermediate buffer, at every level of a
bundle hierarchy:

```csharp
int size = packet.GetByteCount();                 // exact
bool ok = packet.TryWrite(span, out int written); // false (writes nothing) when span is too small
int n = packet.Write(bufferWriter);               // IBufferWriter<byte>: PipeWriter, ArrayBufferWriter, pooled
byte[] bytes = packet.ToArray();
```

`OscWriterOptions` selects the dialect: `ArrayEncoding` (`Standard` or `Ventuz`) and `AsciiOnly`, which throws
`OscFormatException` for a string or address outside 7-bit ASCII instead of encoding UTF-8.

Strings and addresses are always UTF-8 on the wire. OSC 1.0 describes ASCII, which is a subset, so standard peers
are unaffected; peers such as Ventuz that send UTF-8 are read correctly.

For streaming without a model, `OscWriter` is a `ref struct` over a `Span<byte>` with `WriteAddress`, `WriteTypeTags`
and one `Write...` per type. Size the span yourself; writing past the end throws.

## Decoding

```csharp
OscPacket packet = OscPacket.Parse(bytes);                              // throws OscFormatException
if (OscPacket.TryParse(bytes, out OscPacket? p, out OscParseError error)) { ... }
OscMessage message = OscMessage.Parse(bytes);                           // throws if it is a bundle
```

`TryParse` never throws for malformed input; `OscParseError` says what was wrong (truncation, bad padding, invalid
UTF-8, unknown tag, unbalanced array, bad bundle element size, limits exceeded, trailing bytes). Pass a
`ReadOnlyMemory<byte>` instead of a span and blob arguments reference the received buffer in place rather than
copying.

`OscReaderOptions` sets `ArrayEncoding`, `MaxBundleDepth` (default 8; a bundle of messages is depth 1) and
`MaxBundleElements` (default 1024: every message and bundle inside the outermost packet, across all nesting levels).
Both limits protect a receiver from hostile datagrams.

`EnumerateMessages()` on any packet yields its messages in wire order (a bare message yields itself), so a receiver
can treat messages and bundles alike; `OscBundle.IsImmediate` is true only when the bundle and every nested bundle
carry the immediate time tag.

`OscReader` is the allocation-free `ref struct` underneath: `TryReadAddress` returns the address bytes so a router can
match without creating a string, then `TryReadTypeTags`, `TryReadArguments` or the per-type `TryRead...` methods,
and `TryReadBundleHeader` / `TryReadBundleElement` for bundles. Every method returns false and sets `Error` instead of
throwing.

## Current limitations

- Address pattern matching (`*`, `?`, `[...]`, `{...}`) is validated but not evaluated; routing is the application's.
- OSC 1.0 messages that omit the type tag string are rejected (`MissingTypeTags`).
- Time tags are decoded faithfully but never scheduled.
