namespace GameshowPro.Osc.Tests;

[TestClass]
public sealed class OscBundleTests
{
    // Captured from a Ventuz OSC output node: an immediate bundle holding /sound/strike/play with no arguments.
    private const string VentuzPacket = "2362756e646c6500 0000000000000001 00000018 2f736f756e642f737472696b652f706c617900002c000000";

    [TestMethod]
    public void DecodesACapturedVentuzBundle()
    {
        byte[] packet = Hex.Bytes(VentuzPacket);
        OscBundle bundle = OscBundle.Parse(packet);

        Assert.AreEqual(44, packet.Length);
        Assert.IsTrue(bundle.TimeTag.IsImmediate);
        OscMessage message = Assert.IsInstanceOfType<OscMessage>(Assert.ContainsSingle(bundle.Elements));
        Assert.AreEqual("/sound/strike/play", message.Address);
        Assert.IsTrue(message.Arguments.IsEmpty);
        Hex.AssertBytes(VentuzPacket, bundle.ToArray());
    }

    [TestMethod]
    public void NestedBundlesRoundTripAndEnumerateInWireOrder()
    {
        OscBundle bundle = new(new OscTimeTag(0x1122_3344_5566_7788UL),
            new OscMessage("/a", 1),
            new OscBundle(new OscMessage("/b"), new OscBundle(new OscMessage("/c", "deep"))),
            new OscMessage("/d", 2.0));

        byte[] bytes = bundle.ToArray();
        OscBundle decoded = OscBundle.Parse(bytes);

        Assert.AreEqual(bundle, decoded);
        Assert.AreEqual(bytes.Length, bundle.GetByteCount());
        string[] expectedOrder = ["/a", "/b", "/c", "/d"];
        CollectionAssert.AreEqual(expectedOrder, decoded.EnumerateMessages().Select(m => m.Address).ToArray());
        Assert.AreEqual(new OscTimeTag(0x1122_3344_5566_7788UL), decoded.TimeTag);
    }

    [TestMethod]
    public void ElementSizePrefixesAreExact()
    {
        OscMessage a = new("/a");
        OscMessage bc = new("/bc", 1);
        byte[] bytes = new OscBundle(a, bc).ToArray();

        Assert.AreEqual(16 + 4 + a.GetByteCount() + 4 + bc.GetByteCount(), bytes.Length);
        Assert.AreEqual(a.GetByteCount(), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)));
        Assert.AreEqual(bc.GetByteCount(), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20 + a.GetByteCount())));
    }

    [TestMethod]
    public void EmptyBundleIsJustAHeader()
    {
        byte[] bytes = new OscBundle().ToArray();
        Hex.AssertBytes("2362756e646c6500 0000000000000001", bytes);
        Assert.IsTrue(OscBundle.Parse(bytes).Elements.IsEmpty);
        Assert.IsTrue(OscPacket.IsBundle(bytes));
        Assert.IsFalse(OscPacket.IsBundle(new OscMessage("/x").ToArray()));
    }

    [TestMethod]
    public void ScheduledTimeTagsAreDecodedNotRejected()
    {
        OscTimeTag future = OscTimeTag.FromDateTimeOffset(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        OscBundle decoded = OscBundle.Parse(new OscBundle(future, new OscMessage("/later")).ToArray());
        Assert.AreEqual(future, decoded.TimeTag);
        Assert.IsFalse(decoded.TimeTag.IsImmediate);
    }

    [TestMethod]
    public void TypedParseRejectsTheOtherKind()
    {
        _ = Assert.ThrowsExactly<OscFormatException>(() => OscBundle.Parse(new OscMessage("/m").ToArray()));
        _ = Assert.ThrowsExactly<OscFormatException>(() => OscMessage.Parse(new OscBundle().ToArray()));
        Assert.IsFalse(OscMessage.TryParse(new OscBundle().ToArray(), out _, out OscParseError error));
        Assert.AreEqual(OscParseError.None, error);
    }

    [TestMethod]
    public void NullElementsAreRejected()
    {
        ImmutableArray<OscPacket> elements = [null!];
        _ = Assert.ThrowsExactly<ArgumentException>(() => new OscBundle(OscTimeTag.Immediate, elements));
    }
}
