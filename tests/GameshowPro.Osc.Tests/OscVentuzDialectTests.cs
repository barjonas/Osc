namespace GameshowPro.Osc.Tests;

[TestClass]
public sealed class OscVentuzDialectTests
{
    private static readonly OscReaderOptions s_ventuzReader = new() { ArrayEncoding = OscArrayEncoding.VentuzRemoting };
    private static readonly bool[] s_eightTrue = [.. Enumerable.Repeat(true, 8)];
    private static readonly bool[] s_nineTrue = [.. Enumerable.Repeat(true, 9)];
    private static readonly bool[] s_oneZeroOne = [true, false, true];

    [TestMethod]
    public void VentuzArrayIsTypedOnceWithByteAndElementCounts()
    {
        OscMessage message = new("/a", OscValue.FromArray([1, 2, 3]));

        byte[] ventuz = message.ToArray(OscWriterOptions.VentuzRemoting);
        Hex.AssertBytes("2f610000 2c5b695d00000000 0000000c 00000003 00000001 00000002 00000003", ventuz);
        Assert.AreEqual(ventuz.Length, message.GetByteCount(OscWriterOptions.VentuzRemoting));

        byte[] standard = message.ToArray();
        Hex.AssertBytes("2f610000 2c5b6969695d0000 00000001 00000002 00000003", standard);

        OscMessage decoded = OscMessage.Parse(ventuz, s_ventuzReader);
        Assert.AreEqual(message, decoded);
        Hex.AssertBytes(Hex.Of(ventuz), decoded.ToArray(OscWriterOptions.VentuzRemoting));
    }

    [TestMethod]
    public void EmptyVentuzArrayNeedsAnElementTagAndRoundTripsIt()
    {
        _ = Assert.ThrowsExactly<OscFormatException>(() => new OscMessage("/a", OscValue.FromArray([])).ToArray(OscWriterOptions.VentuzRemoting));

        OscMessage typed = new("/a", OscValue.FromArray([], OscTypeTag.Float32));
        byte[] bytes = typed.ToArray(OscWriterOptions.VentuzRemoting);
        Hex.AssertBytes("2f610000 2c5b665d00000000 00000000 00000000", bytes);
        OscMessage decoded = OscMessage.Parse(bytes, s_ventuzReader);
        Assert.IsTrue(decoded.Arguments[0].AsArray().IsEmpty);
        Hex.AssertBytes(Hex.Of(bytes), decoded.ToArray(OscWriterOptions.VentuzRemoting));
    }

    [TestMethod]
    public void VentuzArraysMustBeHomogeneousAndFlat()
    {
        _ = Assert.ThrowsExactly<OscFormatException>(() => new OscMessage("/a", OscValue.FromArray([1, "x"])).ToArray(OscWriterOptions.VentuzRemoting));
        _ = Assert.ThrowsExactly<OscFormatException>(() => new OscMessage("/a", OscValue.FromArray([OscValue.FromArray([1])])).ToArray(OscWriterOptions.VentuzRemoting));
        Assert.IsFalse(OscPacket.TryParse(new OscMessage("/a", OscValue.FromArray([1, "x"])).ToArray(), out _, out OscParseError error, s_ventuzReader));
        Assert.AreEqual(OscParseError.InvalidArray, error);
    }

    [TestMethod]
    public void NullStringsAndLeadingBackslashesUseTheVentuzEscapes()
    {
        OscMessage message = new("/s", OscValue.FromArray(["plain", OscValue.Nil, "\\Folder\\File"]));

        byte[] bytes = message.ToArray(OscWriterOptions.VentuzRemoting);
        OscMessage decoded = OscMessage.Parse(bytes, s_ventuzReader);

        Assert.AreEqual(",[s]", Encoding.ASCII.GetString(bytes, 4, 4));
        string payload = Encoding.UTF8.GetString(bytes, 20, bytes.Length - 20);
        Assert.IsTrue(payload.Contains("plain\0", StringComparison.Ordinal));
        Assert.IsTrue(payload.Contains("\0\\\0", StringComparison.Ordinal));
        Assert.IsTrue(payload.Contains("\\\\Folder\\File\0", StringComparison.Ordinal));
        Assert.AreEqual(message, decoded);
    }

    [TestMethod]
    public void BitVectorsMatchTheVentuzExamples()
    {
        Hex.AssertBytes("01", Encode([]));
        Hex.AssertBytes("1f", Encode([true, true, true, true]));
        Hex.AssertBytes("ff01", Encode(s_eightTrue));
        Hex.AssertBytes("ff03", Encode(s_nineTrue));
        Hex.AssertBytes("0501", Encode([true, false, true, false, false, false, false, false]));

        CollectionAssert.AreEqual(s_eightTrue, Decode([0xFF, 0x00]).ToArray());
        CollectionAssert.AreEqual(s_eightTrue, Decode([0xFF, 0x01]).ToArray());
        CollectionAssert.AreEqual(Array.Empty<bool>(), Decode([0x00]).ToArray());
        CollectionAssert.AreEqual(Array.Empty<bool>(), Decode([]).ToArray());
        CollectionAssert.AreEqual(s_oneZeroOne, Decode([0x0D]).ToArray());

        static byte[] Encode(bool[] bits)
        {
            byte[] packet = new OscMessage("/b", OscValue.FromBitVector([.. bits])).ToArray();
            int length = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(8));
            return packet[12..(12 + length)];
        }

        static ImmutableArray<bool> Decode(byte[] blob)
        {
            byte[] packet = new OscMessage("/b", OscValue.FromBlob(blob)).ToArray();
            packet[5] = (byte)'B';
            return OscMessage.Parse(packet).Arguments[0].AsBitVector();
        }
    }

    [TestMethod]
    public void BitVectorRoundTripsAndIsIndependentOfTheArrayDialect()
    {
        OscMessage message = new("/b", OscValue.FromBitVector([true, false, false, true, true, false, true, false, false, true, true, true]));
        OscMessage decoded = OscMessage.Parse(message.ToArray());
        Assert.AreEqual(message, decoded);
        Assert.AreEqual(message, OscMessage.Parse(message.ToArray(OscWriterOptions.VentuzRemoting), s_ventuzReader));
        Assert.AreEqual(",B", message.TypeTags);
    }
}
