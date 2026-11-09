namespace GameshowPro.Osc.Tests;

[TestClass]
public sealed class OscUdpTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task SendsAndReceivesOverLoopbackWithPacketInformation()
    {
        using OscUdpReceiver receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        Assert.AreNotEqual(0, receiver.LocalEndPoint.Port);
        using OscUdpSender sender = new(receiver.LocalEndPoint);
        OscMessage message = new("/cues/bed-a/play", 1, 0.5f, "fast");
        using CancellationTokenSource timeout = new(s_timeout);

        await sender.SendAsync(message, timeout.Token);
        OscReceivedDatagram datagram = await receiver.Receive(timeout.Token);

        Assert.IsTrue(datagram.IsValid);
        Assert.AreEqual(message, datagram.Packet);
        Assert.AreEqual(OscParseError.None, datagram.Error);
        Assert.AreEqual(message.GetByteCount(), datagram.Length);
        Assert.AreEqual(IPAddress.Loopback, datagram.RemoteEndPoint.Address);
        Assert.AreEqual(IPAddress.Loopback, datagram.DestinationAddress);
        Assert.AreEqual(((IPEndPoint)sender.LocalEndPoint!).Port, datagram.RemoteEndPoint.Port);
        Assert.AreEqual(1L, receiver.ReceivedCount);
        Assert.AreEqual(0L, receiver.MalformedCount);

        NetworkInterface? loopback = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(nic => nic.NetworkInterfaceType == NetworkInterfaceType.Loopback && nic.Supports(NetworkInterfaceComponent.IPv4));
        if (loopback is not null)
        {
            Assert.AreEqual(OscUdpSender.GetInterfaceIndex(loopback, AddressFamily.InterNetwork), datagram.InterfaceIndex);
        }
    }

    [TestMethod]
    public async Task CountsMalformedDatagramsAndStillReportsTheirOrigin()
    {
        using OscUdpReceiver receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        using OscUdpSender sender = new(receiver.LocalEndPoint);
        using CancellationTokenSource timeout = new(s_timeout);

        sender.Send("garbage!"u8);
        OscReceivedDatagram bad = await receiver.Receive(timeout.Token);
        Assert.IsFalse(bad.IsValid);
        Assert.AreEqual(OscParseError.UnexpectedEnd, bad.Error);
        Assert.AreEqual(8, bad.Length);
        Assert.AreEqual(IPAddress.Loopback, bad.RemoteEndPoint.Address);

        sender.Send(new OscBundle(new OscMessage("/a"), new OscMessage("/b", 2)));
        OscReceivedDatagram good = await receiver.Receive(timeout.Token);
        OscBundle bundle = Assert.IsInstanceOfType<OscBundle>(good.Packet);
        string[] expected = ["/a", "/b"];
        CollectionAssert.AreEqual(expected, bundle.EnumerateMessages().Select(m => m.Address).ToArray());

        Assert.AreEqual(2L, receiver.ReceivedCount);
        Assert.AreEqual(1L, receiver.MalformedCount);
    }

    [TestMethod]
    public async Task ReceiveAllYieldsUntilCancelled()
    {
        using OscUdpReceiver receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        using OscUdpSender sender = new(receiver.LocalEndPoint);
        using CancellationTokenSource stop = new(s_timeout);
        List<string> addresses = [];

        await sender.SendAsync(new OscMessage("/one"), stop.Token);
        await sender.SendAsync(new OscMessage("/two"), stop.Token);
        try
        {
            await foreach (OscReceivedDatagram datagram in receiver.ReceiveAll(stop.Token))
            {
                addresses.Add(((OscMessage)datagram.Packet!).Address);
                if (addresses.Count == 2)
                {
                    await stop.CancelAsync();
                }
            }
            Assert.Fail("Enumeration should end by cancellation.");
        }
        catch (OperationCanceledException)
        {
        }

        string[] expected = ["/one", "/two"];
        CollectionAssert.AreEqual(expected, addresses);
    }

    [TestMethod]
    public async Task PerCallDestinationOverridesTheDefault()
    {
        using OscUdpReceiver a = new(new IPEndPoint(IPAddress.Loopback, 0));
        using OscUdpReceiver b = new(new IPEndPoint(IPAddress.Loopback, 0));
        using OscUdpSender sender = new(a.LocalEndPoint);
        using CancellationTokenSource timeout = new(s_timeout);

        await sender.SendAsync(new OscMessage("/to-b"), b.LocalEndPoint, timeout.Token);
        sender.Send(new OscMessage("/to-a"), a.LocalEndPoint);

        Assert.AreEqual("/to-b", ((OscMessage)(await b.Receive(timeout.Token)).Packet!).Address);
        Assert.AreEqual("/to-a", ((OscMessage)(await a.Receive(timeout.Token)).Packet!).Address);
    }

    [TestMethod]
    public void OversizedPacketsAreRefusedBeforeSending()
    {
        using OscUdpSender sender = new(new IPEndPoint(IPAddress.Loopback, 9));
        OscMessage huge = new("/big", OscValue.FromBlob(new byte[OscUdpSender.MaxDatagramLength]));
        _ = Assert.ThrowsExactly<OscFormatException>(() => sender.Send(huge));
    }

    [TestMethod]
    public void SenderBindsToTheRequestedLocalEndPoint()
    {
        using OscUdpSender sender = new(new IPEndPoint(IPAddress.Loopback, 9), new OscUdpSenderOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            MulticastTimeToLive = 4,
        });
        IPEndPoint local = Assert.IsInstanceOfType<IPEndPoint>(sender.LocalEndPoint);
        Assert.AreEqual(IPAddress.Loopback, local.Address);
        Assert.AreNotEqual(0, local.Port);
        _ = Assert.ThrowsExactly<ArgumentException>(() => new OscUdpSender(new IPEndPoint(IPAddress.IPv6Loopback, 9),
            new OscUdpSenderOptions { LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0) }));
    }

    [TestMethod]
    public void ReceiverOptionsValidateTheDatagramLimit()
    {
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OscUdpReceiverOptions { MaxDatagramLength = 4 });
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OscReaderOptions { MaxBundleDepth = 0 });
        Assert.AreEqual(1500, new OscUdpReceiverOptions { MaxDatagramLength = 1500 }.MaxDatagramLength);
    }
}
