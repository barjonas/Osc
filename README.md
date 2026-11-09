# GameshowPro OSC

An [Open Sound Control](https://opensoundcontrol.stanford.edu/) library for .NET 11: an immutable packet model, an
allocation-free reader and writer, UDP send and receive, and stream framing for TCP. It implements every OSC 1.0 and
1.1 type, speaks both Ventuz dialects (scene nodes and Remoting 4) when asked, and has no dependencies. Every path is reflection-free, so it runs
under Native AOT and on the WebAssembly runtime; the trim and AOT analyzers run on each build.

It is the shared OSC implementation for GameshowPro applications and is published as the
[`GameshowPro.Osc`](https://github.com/gameshowpro/Osc/pkgs/nuget/GameshowPro.Osc) NuGet package.

## Projects

| Project | Package | Purpose |
|---|---|---|
| [`src/GameshowPro.Osc`](src/GameshowPro.Osc) | `GameshowPro.Osc` | The library: [packets and values](docs/packets.md), [UDP transport and stream framing](docs/transport.md), [Ventuz dialects](docs/ventuz.md). |
| [`tests/GameshowPro.Osc.Tests`](tests/GameshowPro.Osc.Tests) | — | MSTest suite: specification vectors, a captured Ventuz packet, round trips of every type, malformed input, loopback UDP. |

Implementation notes, design decisions and open items are in [`dev/reference/implementation.md`](dev/reference/implementation.md).

## Quick start

### Packets

```csharp
using GameshowPro.Osc;

// Arguments convert implicitly from the usual CLR types.
OscMessage play = new("/cues/bed-a/play", 1, 0.5f, "fast");

// Bundles nest and encode exactly like messages.
OscBundle bundle = new(new OscMessage("/a"), new OscBundle(new OscMessage("/b", 2)));

// Every packet knows its exact size and writes itself in one pass.
byte[] wire = bundle.ToArray();
OscPacket parsed = OscPacket.Parse(wire);
```

Packets also write into a caller-owned buffer with `GetByteCount()` and `TryWrite(Span<byte>, out int)`, into an
`IBufferWriter<byte>` with `Write(...)`, and parse without throwing through `TryParse`.

### UDP

```csharp
// Send to a unicast destination; the OS picks the interface from its routing table.
using OscUdpSender sender = new(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 8000));
await sender.SendAsync(play, cancellationToken);

// Receive on every interface. Each datagram carries its sender, destination address and ingress interface.
using OscUdpReceiver receiver = new(new IPEndPoint(IPAddress.Any, 8000));
await foreach (OscReceivedDatagram datagram in receiver.ReceiveAll(cancellationToken))
{
    if (datagram.IsValid && datagram.Packet is OscMessage message && message.Arguments is [{ IsNumber: true } level])
    {
        Console.WriteLine($"{message.Address} = {level} via interface {datagram.InterfaceIndex}");
    }
}
```

### Choosing the network interface

The options use the .NET types you already know: an `IPEndPoint` to bind, a `NetworkInterface` for multicast, and
`MulticastOption` to join a group. Nothing here invents its own notion of an adapter.

```csharp
// Pick the adapter and its IPv4 address with the standard APIs.
NetworkInterface nic = NetworkInterface.GetAllNetworkInterfaces().First(n => n.Name == "Ethernet 2");
IPAddress nicAddress = nic.GetIPProperties().UnicastAddresses
    .First(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Address;

// Send multicast from that adapter. Binding to its address selects the outgoing interface for
// unicast and broadcast too; MulticastInterface sets IP_MULTICAST_IF explicitly.
using OscUdpSender multicastSender = new(new IPEndPoint(IPAddress.Parse("239.0.0.1"), 8000), new OscUdpSenderOptions
{
    LocalEndPoint = new IPEndPoint(nicAddress, 0),
    MulticastInterface = nic,
    MulticastTimeToLive = 1,
});
multicastSender.Send(play);

// Listen for unicast on one adapter only: bind its address.
using OscUdpReceiver unicastReceiver = new(new IPEndPoint(nicAddress, 8000));

// Listen for multicast on one adapter: bind IPAddress.Any (the group is never bound), join the group on
// that adapter, and let the datagram's DestinationAddress / InterfaceIndex tell you what arrived where.
using OscUdpReceiver multicastReceiver = new(new IPEndPoint(IPAddress.Any, 8000), new OscUdpReceiverOptions
{
    MulticastGroups = [new MulticastOption(IPAddress.Parse("239.0.0.1"), OscUdpSender.GetInterfaceIndex(nic, AddressFamily.InterNetwork))],
});
OscReceivedDatagram first = await multicastReceiver.Receive(cancellationToken);
bool fromOurAdapter = first.InterfaceIndex == OscUdpSender.GetInterfaceIndex(nic, AddressFamily.InterNetwork)
    && first.DestinationAddress.Equals(IPAddress.Parse("239.0.0.1"));
```

### TCP

UDP needs no framing (one datagram is one packet); a stream does. `OscStreamConnection` wraps any duplex `Stream`
with the length prefix used by OSC 1.0 over TCP and Ventuz Remoting, or SLIP as OSC 1.1 recommends. Connecting is
left to `TcpClient` so the host's own socket, TLS or retry handling applies; bind the client to an adapter's address
to choose the interface.

```csharp
using TcpClient client = new(new IPEndPoint(nicAddress, 0)); // local end point selects the interface
await client.ConnectAsync("ventuz-host", 19400, cancellationToken);
await using OscStreamConnection connection = new(client.GetStream(), OscStreamConnectionOptions.VentuzRemoting);

await connection.SendAsync(play, cancellationToken);
await foreach (OscPacket packet in connection.ReceiveAll(cancellationToken)) // ends when the peer closes
{
    Console.WriteLine(packet);
}
```

`OscStreamConnectionOptions` selects the framing, both dialects and a `MaxPacketLength` guard. When you already own
the buffering (a `PipeReader` loop, a custom protocol), `OscStreamFramer` exposes the same framing over spans and
`ReadOnlySequence<byte>` without any copying.

## Building

Requires the .NET SDK version pinned in [`global.json`](global.json).

```powershell
dotnet test src/GameshowPro.Osc.slnx -c Release
dotnet pack src/GameshowPro.Osc.slnx -c Release
```

[CI](.github/workflows/build.yaml) runs the tests, packs, tags the release with Nerdbank.GitVersioning and pushes the
package on every push to `main`.
