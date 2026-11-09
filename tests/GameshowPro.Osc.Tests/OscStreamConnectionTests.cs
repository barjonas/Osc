namespace GameshowPro.Osc.Tests;

[TestClass]
public sealed class OscStreamConnectionTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private static async Task<(OscStreamConnection Client, OscStreamConnection Server, TcpClient ClientSocket, TcpClient ServerSocket)> ConnectPair(
        OscStreamConnectionOptions? options, CancellationToken cancellationToken)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            TcpClient clientSocket = new();
            await clientSocket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, cancellationToken);
            TcpClient serverSocket = await listener.AcceptTcpClientAsync(cancellationToken);
            return (new OscStreamConnection(clientSocket.GetStream(), options), new OscStreamConnection(serverSocket.GetStream(), options), clientSocket, serverSocket);
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    [DataRow(OscStreamFraming.LengthPrefixed)]
    [DataRow(OscStreamFraming.Slip)]
    public async Task PacketsRoundTripOverTcpInBothFramings(OscStreamFraming framing)
    {
        using CancellationTokenSource timeout = new(s_timeout);
        OscStreamConnectionOptions options = new() { Framing = framing };
        (OscStreamConnection client, OscStreamConnection server, TcpClient clientSocket, TcpClient serverSocket) = await ConnectPair(options, timeout.Token);
        await using (client)
        await using (server)
        using (clientSocket)
        using (serverSocket)
        {
            // Blob bytes include SLIP END and ESC so escaping is exercised.
            OscMessage first = new("/first", 1, OscValue.FromBlob(new byte[] { 0xC0, 0xDB, 0xC0 }));
            OscBundle second = new(new OscMessage("/second", "two"), new OscBundle(new OscMessage("/third", 3.0)));

            await client.SendAsync(first, timeout.Token);
            client.Send(second);

            Assert.AreEqual(first, await server.Receive(timeout.Token));
            Assert.AreEqual(second, await server.Receive(timeout.Token));
            Assert.AreEqual(2L, client.SentCount);
            Assert.AreEqual(2L, server.ReceivedCount);

            await server.SendAsync(new OscMessage("/reply"), timeout.Token);
            Assert.AreEqual(new OscMessage("/reply"), await client.Receive(timeout.Token));
        }
    }

    [TestMethod]
    public async Task FramesSplitAcrossWritesAndCoalescedWritesAreBothHandled()
    {
        using CancellationTokenSource timeout = new(s_timeout);
        (OscStreamConnection client, OscStreamConnection server, TcpClient clientSocket, TcpClient serverSocket) = await ConnectPair(null, timeout.Token);
        await using (client)
        await using (server)
        using (clientSocket)
        using (serverSocket)
        {
            OscMessage a = new("/a", 1);
            OscMessage b = new("/b", 2);
            ArrayBufferWriter<byte> frames = new();
            _ = OscStreamFramer.WriteFrame(frames, a, OscStreamFraming.LengthPrefixed);
            _ = OscStreamFramer.WriteFrame(frames, b, OscStreamFraming.LengthPrefixed);
            NetworkStream raw = clientSocket.GetStream();

            // Two frames in one write, the first split mid-prefix and mid-body.
            await raw.WriteAsync(frames.WrittenMemory[..2], timeout.Token);
            await raw.FlushAsync(timeout.Token);
            await Task.Delay(50, timeout.Token);
            await raw.WriteAsync(frames.WrittenMemory[2..9], timeout.Token);
            await raw.FlushAsync(timeout.Token);
            await Task.Delay(50, timeout.Token);
            await raw.WriteAsync(frames.WrittenMemory[9..], timeout.Token);
            await raw.FlushAsync(timeout.Token);

            Assert.AreEqual(a, await server.Receive(timeout.Token));
            Assert.AreEqual(b, await server.Receive(timeout.Token));
        }
    }

    [TestMethod]
    public async Task CleanCloseEndsEnumerationAndTruncatedFrameThrows()
    {
        using CancellationTokenSource timeout = new(s_timeout);
        (OscStreamConnection client, OscStreamConnection server, TcpClient clientSocket, TcpClient serverSocket) = await ConnectPair(null, timeout.Token);
        await using (server)
        using (serverSocket)
        {
            await client.SendAsync(new OscMessage("/only"), timeout.Token);
            await client.DisposeAsync();
            clientSocket.Dispose();

            List<OscPacket> received = [];
            await foreach (OscPacket packet in server.ReceiveAll(timeout.Token))
            {
                received.Add(packet);
            }
            Assert.AreEqual(1, received.Count);
            Assert.IsNull(await server.Receive(timeout.Token));
        }

        (client, server, clientSocket, serverSocket) = await ConnectPair(null, timeout.Token);
        await using (server)
        using (serverSocket)
        {
            byte[] half = new OscBundle(new OscMessage("/x")).ToArray()[..10];
            byte[] prefixed = [0, 0, 0, 24, .. half];
            await clientSocket.GetStream().WriteAsync(prefixed, timeout.Token);
            await client.DisposeAsync();
            clientSocket.Dispose();

            OscFormatException error = await Assert.ThrowsExactlyAsync<OscFormatException>(async () => await server.Receive(timeout.Token));
            Assert.AreEqual(OscParseError.UnexpectedEnd, error.Error);
        }
    }

    [TestMethod]
    public async Task OversizedPrefixFailsFastInsteadOfBuffering()
    {
        using CancellationTokenSource timeout = new(s_timeout);
        (OscStreamConnection client, OscStreamConnection server, TcpClient clientSocket, TcpClient serverSocket) =
            await ConnectPair(new OscStreamConnectionOptions { MaxPacketLength = 64 }, timeout.Token);
        await using (client)
        await using (server)
        using (clientSocket)
        using (serverSocket)
        {
            byte[] prefix = [0, 0, 1, 0, 1, 2, 3, 4];
            await clientSocket.GetStream().WriteAsync(prefix, timeout.Token);
            _ = await Assert.ThrowsExactlyAsync<OscFormatException>(async () => await server.Receive(timeout.Token));
        }
    }

    [TestMethod]
    public void SequenceFramersMatchTheSpanFramersAcrossSegments()
    {
        OscMessage message = new("/seq", OscValue.FromBlob(new byte[] { 0xC0, 0xDB }), "text");
        ArrayBufferWriter<byte> prefixed = new();
        ArrayBufferWriter<byte> slip = new();
        _ = OscStreamFramer.WriteFrame(prefixed, message, OscStreamFraming.LengthPrefixed);
        _ = OscStreamFramer.WriteFrame(slip, message, OscStreamFraming.Slip);

        ReadOnlySequence<byte> prefixedSequence = Segmented(prefixed.WrittenSpan.ToArray(), 3);
        Assert.IsTrue(OscStreamFramer.TryReadLengthPrefixedFrame(prefixedSequence, out ReadOnlySequence<byte> packet, out SequencePosition consumed));
        Assert.AreEqual(message, OscPacket.Parse(packet));
        Assert.AreEqual(0L, prefixedSequence.Slice(consumed).Length);
        Assert.IsFalse(OscStreamFramer.TryReadLengthPrefixedFrame(prefixedSequence.Slice(0, prefixedSequence.Length - 1), out _, out _));

        ReadOnlySequence<byte> slipSequence = Segmented(slip.WrittenSpan.ToArray(), 2);
        ArrayBufferWriter<byte> decoded = new();
        Assert.IsTrue(OscStreamFramer.TryReadSlipFrame(slipSequence, decoded, out int length, out consumed));
        Assert.AreEqual(message, OscPacket.Parse(decoded.WrittenSpan[..length]));
        Assert.AreEqual(0L, slipSequence.Slice(consumed).Length);
        Assert.IsFalse(OscStreamFramer.TryReadSlipFrame(slipSequence.Slice(0, slipSequence.Length - 1), new ArrayBufferWriter<byte>(), out _, out _));

        Assert.AreEqual(message, OscPacket.Parse(Segmented(message.ToArray(), 5)));
    }

    [TestMethod]
    public void EnumerateMessagesAndIsImmediateCoverBareMessagesAndNestedBundles()
    {
        OscMessage bare = new("/bare");
        Assert.AreEqual(bare, Assert.ContainsSingle(bare.EnumerateMessages()));

        OscBundle immediate = new(new OscMessage("/a"), new OscBundle(new OscMessage("/b")));
        OscBundle scheduled = new(OscTimeTag.Immediate, new OscMessage("/a"), new OscBundle(new OscTimeTag(5), new OscMessage("/b")));
        Assert.IsTrue(immediate.IsImmediate);
        Assert.IsFalse(scheduled.IsImmediate);
        Assert.AreEqual(2, ((OscPacket)scheduled).EnumerateMessages().Count());
    }

    /// <summary>Splits <paramref name="bytes"/> into linked segments of <paramref name="segmentLength"/> bytes.</summary>
    private static ReadOnlySequence<byte> Segmented(byte[] bytes, int segmentLength)
    {
        Segment first = new(bytes.AsMemory(0, Math.Min(segmentLength, bytes.Length)), 0);
        Segment last = first;
        for (int offset = segmentLength; offset < bytes.Length; offset += segmentLength)
        {
            last = last.Append(bytes.AsMemory(offset, Math.Min(segmentLength, bytes.Length - offset)));
        }
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            Segment next = new(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
