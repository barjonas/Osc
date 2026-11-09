namespace GameshowPro.Osc;

/// <summary>Socket settings for an <see cref="OscUdpReceiver"/>. Every member maps directly onto a <see cref="Socket"/> option.</summary>
public sealed record OscUdpReceiverOptions
{
    public static OscUdpReceiverOptions Default { get; } = new();

    /// <summary>IPv4 multicast groups to join at start, each with the local address or interface index to join on.</summary>
    public ImmutableArray<MulticastOption> MulticastGroups { get; init; } = [];

    /// <summary>IPv6 multicast groups to join at start.</summary>
    public ImmutableArray<IPv6MulticastOption> IPv6MulticastGroups { get; init; } = [];

    /// <summary>Lets other sockets bind the same port (<c>SO_REUSEADDR</c>), as multicast listeners usually need.</summary>
    public bool ReuseAddress { get; init; } = true;

    /// <summary>Accepts datagrams sent to a broadcast address (<c>SO_BROADCAST</c>). IPv4 only.</summary>
    public bool EnableBroadcast { get; init; } = true;

    /// <summary>See <see cref="OscUdpSenderOptions.MulticastLoopback"/>.</summary>
    public bool MulticastLoopback { get; init; } = true;

    /// <summary>Kernel receive buffer size (<c>SO_RCVBUF</c>). Null keeps the stack default.</summary>
    public int? ReceiveBufferSize { get; init; }

    /// <summary>Largest datagram accepted. Longer datagrams are counted as malformed and dropped. Defaults to 65535.</summary>
    public int MaxDatagramLength
    {
        get;
        init => field = value is >= OscBinary.MinimumMessageLength and <= ushort.MaxValue
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxDatagramLength), value, "Must be between 8 and 65535.");
    } = ushort.MaxValue;

    /// <summary>Dialect and limits used to decode packets.</summary>
    public OscReaderOptions Reader { get; init; } = OscReaderOptions.Default;
}

/// <summary>One datagram received by an <see cref="OscUdpReceiver"/>, decoded or rejected.</summary>
/// <param name="Packet">The decoded packet, or null when <paramref name="Error"/> explains why it was rejected.</param>
/// <param name="Error">Why the datagram could not be decoded, or <see cref="OscParseError.None"/>.</param>
/// <param name="Length">Bytes in the datagram.</param>
/// <param name="RemoteEndPoint">The sender.</param>
/// <param name="DestinationAddress">
/// The address the datagram was sent to, which tells a unicast, multicast or broadcast destination apart when
/// one socket serves several.
/// </param>
/// <param name="InterfaceIndex">The OS index of the interface the datagram arrived on.</param>
public sealed record OscReceivedDatagram(
    OscPacket? Packet,
    OscParseError Error,
    int Length,
    IPEndPoint RemoteEndPoint,
    IPAddress DestinationAddress,
    int InterfaceIndex)
{
    [MemberNotNullWhen(true, nameof(Packet))]
    public bool IsValid => Packet is not null;
}

/// <summary>
/// Receives OSC over UDP on one local port. Each call to <see cref="Receive"/> returns one datagram with its
/// sender, destination address and ingress interface, so a host that listens on <see cref="IPAddress.Any"/>
/// can attribute traffic to the logical listener it was meant for and ignore ambiguous arrivals.
/// </summary>
/// <remarks>
/// Bind to <see cref="IPAddress.Any"/> (not the group address) to receive multicast or broadcast, join groups
/// through <see cref="OscUdpReceiverOptions.MulticastGroups"/> or <see cref="JoinMulticastGroup(MulticastOption)"/>,
/// and use <see cref="OscReceivedDatagram.DestinationAddress"/> and <see cref="OscReceivedDatagram.InterfaceIndex"/>
/// to filter. Only one <see cref="Receive"/> call should be in flight at a time.
/// </remarks>
public sealed class OscUdpReceiver : IDisposable
{
    private readonly Socket _socket;
    private readonly OscUdpReceiverOptions _options;
    private readonly EndPoint _anyEndPoint;
    private long _received;
    private long _malformed;

    public OscUdpReceiver(IPEndPoint localEndPoint, OscUdpReceiverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        _options = options ?? OscUdpReceiverOptions.Default;
        bool ipv6 = localEndPoint.AddressFamily == AddressFamily.InterNetworkV6;
        _anyEndPoint = new IPEndPoint(ipv6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        _socket = new(localEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (_options.ReuseAddress)
            {
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }
            // Ask the stack for the destination address and interface of every datagram.
            _socket.SetSocketOption(ipv6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            if (!ipv6)
            {
                _socket.EnableBroadcast = _options.EnableBroadcast;
            }
            _socket.MulticastLoopback = _options.MulticastLoopback;
            if (_options.ReceiveBufferSize is int size)
            {
                _socket.ReceiveBufferSize = size;
            }
            _socket.Bind(localEndPoint);
            LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
            foreach (MulticastOption group in _options.MulticastGroups.IsDefault ? [] : _options.MulticastGroups)
            {
                JoinMulticastGroup(group);
            }
            foreach (IPv6MulticastOption group in _options.IPv6MulticastGroups.IsDefault ? [] : _options.IPv6MulticastGroups)
            {
                JoinMulticastGroup(group);
            }
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    /// <summary>The bound address and port, with the actual port when 0 was requested.</summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>Datagrams received, including malformed ones.</summary>
    public long ReceivedCount => Interlocked.Read(ref _received);

    /// <summary>Datagrams that were not valid OSC or exceeded <see cref="OscUdpReceiverOptions.MaxDatagramLength"/>.</summary>
    public long MalformedCount => Interlocked.Read(ref _malformed);

    public void JoinMulticastGroup(MulticastOption group) => _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, group);
    public void DropMulticastGroup(MulticastOption group) => _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, group);
    public void JoinMulticastGroup(IPv6MulticastOption group) => _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, group);
    public void DropMulticastGroup(IPv6MulticastOption group) => _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.DropMembership, group);

    /// <summary>Waits for the next datagram. Throws <see cref="ObjectDisposedException"/> once the receiver is disposed.</summary>
    public async ValueTask<OscReceivedDatagram> Receive(CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.MaxDatagramLength);
        try
        {
            while (true)
            {
                SocketReceiveMessageFromResult result;
                try
                {
                    result = await _socket.ReceiveMessageFromAsync(buffer.AsMemory(0, _options.MaxDatagramLength), SocketFlags.None,
                        _anyEndPoint, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // Winsock reports an ICMP "port unreachable" for an earlier send on this socket; it is not an incoming datagram.
                    continue;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
                {
                    _ = Interlocked.Increment(ref _received);
                    _ = Interlocked.Increment(ref _malformed);
                    continue;
                }
                _ = Interlocked.Increment(ref _received);
                (OscPacket? packet, OscParseError error) = Decode(buffer, result.ReceivedBytes, _options.Reader);
                if (packet is null)
                {
                    _ = Interlocked.Increment(ref _malformed);
                }
                return new(packet, error, result.ReceivedBytes, (IPEndPoint)result.RemoteEndPoint,
                    result.PacketInformation.Address, result.PacketInformation.Interface);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        static (OscPacket? Packet, OscParseError Error) Decode(byte[] buffer, int length, OscReaderOptions options)
            => OscPacket.TryParse(buffer.AsSpan(0, length), out OscPacket? packet, out OscParseError error, options)
                ? (packet, OscParseError.None)
                : (null, error);
    }

    /// <summary>Yields datagrams until <paramref name="cancellationToken"/> is cancelled or the receiver is disposed.</summary>
    public async IAsyncEnumerable<OscReceivedDatagram> ReceiveAll([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            yield return await Receive(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => _socket.Dispose();
}
