# Transport

## UDP

`OscUdpSender` and `OscUdpReceiver` are thin, explicit wrappers over `System.Net.Sockets.Socket`. Their options use
the .NET types you already know (`IPEndPoint`, `NetworkInterface`, `MulticastOption`) and every option maps onto one
socket option, so there is nothing new to learn about interface selection.

### Sending

```csharp
using OscUdpSender sender = new(new IPEndPoint(IPAddress.Parse("239.0.0.1"), 8000), new OscUdpSenderOptions
{
    LocalEndPoint = new IPEndPoint(IPAddress.Parse("192.168.1.10"), 0), // bind: picks the outgoing interface
    MulticastInterface = nic,                                           // or name the interface explicitly
    MulticastTimeToLive = 1,
    Writer = OscWriterOptions.VentuzRemoting,                                   // dialect, when the peer needs it
});
sender.Send(message);                        // synchronous
await sender.SendAsync(message, token);      // asynchronous
await sender.SendAsync(message, otherEndPoint, token); // per-call destination, e.g. a reply
```

Each send encodes the packet into a pooled buffer in one pass. A packet over 65 507 bytes is refused with
`OscFormatException` before anything is sent. `Send(ReadOnlySpan<byte>)` / `SendAsync(ReadOnlyMemory<byte>)` send
bytes you have already encoded.

- **Interface selection.** Binding `LocalEndPoint` to an address on the wanted interface selects it for unicast and
  broadcast and, for IPv4, also for multicast. `MulticastInterface` sets `IP_MULTICAST_IF` from the
  `NetworkInterface` and wins over the bound address. `OscUdpSender.GetInterfaceIndex(nic, family)` exposes the OS
  index the socket options use.
- **Broadcast** is enabled by default (`EnableBroadcast`); the destination decides whether it is used.
- **Multicast loopback** is on by default so a receiver on the same host hears the sender. Winsock applies this on the
  receiving socket and POSIX on the sending one, so both sides set it.

### Receiving

```csharp
using OscUdpReceiver receiver = new(new IPEndPoint(IPAddress.Any, 8000), new OscUdpReceiverOptions
{
    MulticastGroups = [new MulticastOption(IPAddress.Parse("239.0.0.1"), localAddress)],
    Reader = new OscReaderOptions { MaxBundleDepth = 4, MaxBundleElements = 32 },
});

OscReceivedDatagram datagram = await receiver.Receive(token);
await foreach (OscReceivedDatagram d in receiver.ReceiveAll(token)) { ... }
```

Each `OscReceivedDatagram` carries the decoded `Packet` (or an `OscParseError` when `IsValid` is false), the
datagram `Length`, the `RemoteEndPoint`, the `DestinationAddress` it was sent to and the `InterfaceIndex` it arrived
on. `ReceivedCount` and `MalformedCount` count datagrams. Only one `Receive` call should be in flight at a time.

The receiver applies what Soundling learned running OSC in production:

- **Bind to `IPAddress.Any`**, not the group or a specific address, when you want multicast or broadcast, and join
  groups with `MulticastGroups` or `JoinMulticastGroup`. The stack's packet information (`IP_PKTINFO`, enabled
  automatically) then tells you the real destination and ingress interface of every datagram, so one socket per port
  can serve several logical listeners and you can refuse traffic that arrived on an interface or address you did not
  intend to listen on. Ambiguous ingress should not execute a command.
- **`SO_REUSEADDR` is on by default** so several processes can share a multicast port.
- **Malformed datagrams are counted, not thrown.** Hostile or broken input costs one failed `TryParse`.
- **Windows ICMP "port unreachable"** errors raised against the socket by an earlier send are swallowed; a datagram
  larger than `MaxDatagramLength` is counted as malformed and dropped.
- **Decoding limits** (`Reader`) bound nesting depth and element count per datagram.
- Rebinding after a network change, retry loops, routing and queueing are deliberately left to the application: they
  depend on its threading model and configuration store.

`OscUdpReceiver` disposes its socket; a pending `Receive` then fails with `ObjectDisposedException`. Pass a
`CancellationToken` for an orderly stop.

## TCP and other streams

UDP needs no framing: one datagram is one packet. On a stream, packets are delimited one of two ways:

- `OscStreamFraming.LengthPrefixed` — a big-endian int32 length before each packet (OSC 1.0 over TCP, Ventuz Remoting).
- `OscStreamFraming.Slip` — SLIP (RFC 1055) `END`/`ESC` framing as OSC 1.1 recommends.

### `OscStreamConnection`

Wraps any duplex `Stream` (a `NetworkStream` from `TcpClient` or `TcpListener`, an SSL stream, a pipe) and does the
framing, buffering and decoding:

```csharp
await using OscStreamConnection connection = new(client.GetStream(), new OscStreamConnectionOptions
{
    Framing = OscStreamFraming.LengthPrefixed,   // the default
    MaxPacketLength = 64 * 1024,                 // a longer prefix fails the connection instead of buffering
});
await connection.SendAsync(packet, token);       // sends are serialized; several callers may send at once
OscPacket? next = await connection.Receive(token); // null when the peer closed cleanly between frames
await foreach (OscPacket p in connection.ReceiveAll(token)) { ... }
```

`OscStreamConnectionOptions.VentuzRemoting` selects the Remoting dialect for both directions. A malformed packet or
corrupt framing throws `OscFormatException` from `Receive`; a stream cannot be resynchronized after that, so close
it. Disposing the connection disposes the stream unless `LeaveOpen` is set. Connecting, binding to an interface,
TLS and reconnection stay with the host.

### `OscStreamFramer`

The framing primitives the connection is built on, for hosts that already own a `PipeReader` loop or speak a
protocol that carries OSC inside something else:

```csharp
OscStreamFramer.WriteFrame(bufferWriter, packet, OscStreamFraming.LengthPrefixed);

// Spans:
if (OscStreamFramer.TryReadLengthPrefixedFrame(span, out ReadOnlySpan<byte> packetBytes, out int consumed)) { ... }
if (OscStreamFramer.TryReadSlipFrame(span, scratch, out int packetLength, out int consumed)) { ... }

// PipeReader buffers, without copying across segments:
if (OscStreamFramer.TryReadLengthPrefixedFrame(sequence, out ReadOnlySequence<byte> frame, out SequencePosition end)) { ... }
if (OscStreamFramer.TryReadSlipFrame(sequence, bufferWriter, out int packetLength, out SequencePosition end)) { ... }
```

The `TryRead...` methods return false when the buffer does not yet hold a complete frame and throw
`OscFormatException` when the stream is corrupt (a length that cannot be a packet or exceeds the optional
`maxPacketLength`, a bad SLIP escape). `OscPacket.Parse` and `TryParse` accept a `ReadOnlySequence<byte>` directly.
