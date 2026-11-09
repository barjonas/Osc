namespace GameshowPro.Osc;

/// <summary>Socket settings for an <see cref="OscUdpSender"/>. Every member maps directly onto a <see cref="Socket"/> option.</summary>
public sealed record OscUdpSenderOptions
{
    public static OscUdpSenderOptions Default { get; } = new();

    /// <summary>
    /// Binds the socket to this local address and port before sending. Choosing an address on a particular
    /// network interface makes that interface the outgoing one, and for IPv4 multicast it also selects the
    /// multicast interface unless <see cref="MulticastInterface"/> says otherwise. Null lets the stack choose.
    /// </summary>
    public IPEndPoint? LocalEndPoint { get; init; }

    /// <summary>The interface that carries outgoing multicast datagrams (<c>IP_MULTICAST_IF</c>).</summary>
    public NetworkInterface? MulticastInterface { get; init; }

    /// <summary>Hop limit for multicast datagrams (<c>IP_MULTICAST_TTL</c>). Null keeps the stack default of 1.</summary>
    public int? MulticastTimeToLive { get; init; }

    /// <summary>Allows sending to a broadcast address (<c>SO_BROADCAST</c>). IPv4 only.</summary>
    public bool EnableBroadcast { get; init; } = true;

    /// <summary>
    /// Whether multicast datagrams sent from this host are delivered to receivers on the same host
    /// (<c>IP_MULTICAST_LOOP</c>). Winsock applies this on the receiving socket and POSIX on the sending one,
    /// so both <see cref="OscUdpSender"/> and <see cref="OscUdpReceiver"/> set it.
    /// </summary>
    public bool MulticastLoopback { get; init; } = true;

    /// <summary>Dialect used to encode packets.</summary>
    public OscWriterOptions Writer { get; init; } = OscWriterOptions.Default;
}

/// <summary>
/// Sends packets to one destination over UDP. Each send encodes the packet into a pooled buffer in a single pass.
/// The destination may be unicast, multicast or broadcast; a per-call destination overload covers replies.
/// </summary>
public sealed class OscUdpSender : IDisposable
{
    /// <summary>Largest payload one IPv4 UDP datagram can carry.</summary>
    public const int MaxDatagramLength = 65507;

    private readonly Socket _socket;
    private readonly OscUdpSenderOptions _options;

    public OscUdpSender(IPEndPoint remoteEndPoint, OscUdpSenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        RemoteEndPoint = remoteEndPoint;
        _options = options ?? OscUdpSenderOptions.Default;
        _socket = new(remoteEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            Configure();
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    public IPEndPoint RemoteEndPoint { get; }

    /// <summary>The address and port the socket is bound to, once it has been bound by configuration or a first send.</summary>
    public EndPoint? LocalEndPoint => _socket.LocalEndPoint;

    public void Send(OscPacket packet) => Send(packet, RemoteEndPoint);

    public void Send(OscPacket packet, IPEndPoint destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int count = Measure(packet);
        byte[] rented = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            _ = packet.TryWrite(rented.AsSpan(0, count), out _, _options.Writer);
            _ = _socket.SendTo(rented.AsSpan(0, count), SocketFlags.None, destination);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Sends bytes that are already an encoded packet.</summary>
    public void Send(ReadOnlySpan<byte> datagram)
    {
        ValidateLength(datagram.Length);
        _ = _socket.SendTo(datagram, SocketFlags.None, RemoteEndPoint);
    }

    public ValueTask SendAsync(OscPacket packet, CancellationToken cancellationToken = default)
        => SendAsync(packet, RemoteEndPoint, cancellationToken);

    public async ValueTask SendAsync(OscPacket packet, IPEndPoint destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int count = Measure(packet);
        byte[] rented = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            _ = packet.TryWrite(rented.AsSpan(0, count), out _, _options.Writer);
            _ = await _socket.SendToAsync(rented.AsMemory(0, count), SocketFlags.None, destination, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Sends bytes that are already an encoded packet.</summary>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default)
    {
        ValidateLength(datagram.Length);
        _ = await _socket.SendToAsync(datagram, SocketFlags.None, RemoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _socket.Dispose();

    /// <summary>The OS interface index of <paramref name="networkInterface"/> for <paramref name="addressFamily"/>.</summary>
    public static int GetInterfaceIndex(NetworkInterface networkInterface, AddressFamily addressFamily)
    {
        ArgumentNullException.ThrowIfNull(networkInterface);
        IPInterfaceProperties properties = networkInterface.GetIPProperties();
        return addressFamily switch
        {
            AddressFamily.InterNetwork => properties.GetIPv4Properties().Index,
            AddressFamily.InterNetworkV6 => properties.GetIPv6Properties().Index,
            _ => throw new ArgumentOutOfRangeException(nameof(addressFamily), addressFamily, "Only IPv4 and IPv6 interfaces have an index."),
        };
    }

    private void Configure()
    {
        bool ipv6 = RemoteEndPoint.AddressFamily == AddressFamily.InterNetworkV6;
        if (_options.LocalEndPoint is { } local)
        {
            if (local.AddressFamily != RemoteEndPoint.AddressFamily)
            {
                throw new ArgumentException("The local and remote end points must use the same address family.", nameof(_options));
            }
            _socket.Bind(local);
        }
        if (!ipv6)
        {
            _socket.EnableBroadcast = _options.EnableBroadcast;
        }
        _socket.MulticastLoopback = _options.MulticastLoopback;
        if (_options.MulticastInterface is { } networkInterface)
        {
            int index = GetInterfaceIndex(networkInterface, RemoteEndPoint.AddressFamily);
            if (ipv6)
            {
                _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, index);
            }
            else
            {
                // IP_MULTICAST_IF takes an interface index in network byte order, exactly as UdpClient does.
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.HostToNetworkOrder(index));
            }
        }
        else if (!ipv6 && _options.LocalEndPoint is { Address: { } address } && !address.Equals(IPAddress.Any))
        {
            // IP_MULTICAST_IF also accepts the interface's own IPv4 address as a 4-byte in_addr.
            Span<byte> bytes = stackalloc byte[4];
            _ = address.TryWriteBytes(bytes, out _);
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, MemoryMarshal.Read<int>(bytes));
        }
        if (_options.MulticastTimeToLive is int ttl)
        {
            _socket.SetSocketOption(ipv6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, ttl);
        }
    }

    private int Measure(OscPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        int count = packet.GetByteCount(_options.Writer);
        ValidateLength(count);
        return count;
    }

    private static void ValidateLength(int length)
    {
        if (length > MaxDatagramLength)
        {
            throw new OscFormatException($"The packet is {length} bytes; one UDP datagram carries at most {MaxDatagramLength}.");
        }
    }
}
