namespace GameshowPro.Osc.Tests;

[TestClass]
public sealed class OscMessageCodecTests
{
    // The two worked examples from the OSC 1.0 specification.
    private const string SpecExample1 = "2f6f7363696c6c61746f722f342f6672657175656e637900 2c660000 43dc0000";
    private const string SpecExample2 = "2f666f6f00000000 2c6969736666 0000 000003e8 ffffffff 68656c6c6f000000 3f9df3b6 40b5b22d";

    [TestMethod]
    public void EncodesTheSpecificationExamples()
    {
        Hex.AssertBytes(SpecExample1, new OscMessage("/oscillator/4/frequency", 440.0f).ToArray());
        Hex.AssertBytes(SpecExample2, new OscMessage("/foo", 1000, -1, "hello", 1.234f, 5.678f).ToArray());
    }

    [TestMethod]
    public void DecodesTheSpecificationExamples()
    {
        OscMessage first = OscMessage.Parse(Hex.Bytes(SpecExample1));
        Assert.AreEqual("/oscillator/4/frequency", first.Address);
        Assert.AreEqual(",f", first.TypeTags);
        Assert.AreEqual(440.0f, Assert.ContainsSingle(first.Arguments).AsSingle());

        OscMessage second = OscMessage.Parse(Hex.Bytes(SpecExample2));
        Assert.AreEqual(new OscMessage("/foo", 1000, -1, "hello", 1.234f, 5.678f), second);
        Assert.AreEqual(",iisff", second.TypeTags);
    }

    [TestMethod]
    public void EveryTypeRoundTrips()
    {
        OscMessage original = new("/all/types",
            OscValue.FromInt32(int.MinValue),
            OscValue.FromSingle(-1.5f),
            OscValue.FromString("ünïcödé ✓"),
            OscValue.FromBlob(new byte[] { 1, 2, 3, 4, 5 }),
            OscValue.FromInt64(long.MaxValue),
            OscValue.FromTimeTag(new OscTimeTag(0x1234_5678_9ABC_DEF0UL)),
            OscValue.FromDouble(Math.PI),
            OscValue.FromSymbol("sym"),
            OscValue.FromChar('Z'),
            OscValue.FromColor(new OscColor(0x11, 0x22, 0x33, 0x44)),
            OscValue.FromMidi(new OscMidiMessage(1, 0x90, 60, 127)),
            OscValue.True,
            OscValue.False,
            OscValue.Nil,
            OscValue.Infinitum,
            OscValue.FromArray([1, OscValue.FromArray(["nested", 2.0]), OscValue.FromBlob(ReadOnlyMemory<byte>.Empty)]),
            OscValue.FromBitVector([true, false, true, true, false, false, false, false, true, true]),
            OscValue.FromString(""));

        byte[] bytes = original.ToArray();
        OscMessage decoded = OscMessage.Parse(bytes);

        Assert.AreEqual(",ifsbhtdScrmTFNI[i[sd]b]Bs", original.TypeTags);
        Assert.AreEqual(original, decoded);
        Assert.AreEqual(original.GetHashCode(), decoded.GetHashCode());
        Assert.AreEqual(bytes.Length, original.GetByteCount());
        Assert.AreEqual(0, bytes.Length % 4);
        Hex.AssertBytes(Hex.Of(bytes), decoded.ToArray());
    }

    [TestMethod]
    public void StringsAreUtf8OnTheWire()
    {
        byte[] bytes = new OscMessage("/s", "é").ToArray();
        // "/s\0\0" ",s\0\0" then C3 A9 00 00
        Hex.AssertBytes("2f730000 2c730000 c3a90000", bytes);
        Assert.AreEqual("é", OscMessage.Parse(bytes).Arguments[0].AsString());
    }

    [TestMethod]
    public void AsciiOnlyRejectsNonAsciiStringsAndAddresses()
    {
        OscWriterOptions ascii = new() { AsciiOnly = true };
        _ = Assert.ThrowsExactly<OscFormatException>(() => new OscMessage("/s", "é").ToArray(ascii));
        _ = Assert.ThrowsExactly<OscFormatException>(() => new OscMessage("/é").GetByteCount(ascii));
        Hex.AssertBytes("2f730000 2c730000 61000000", new OscMessage("/s", "a").ToArray(ascii));
    }

    [TestMethod]
    public void TryWriteRefusesASmallBufferAndFillsAnExactOne()
    {
        OscMessage message = new("/x", 1, "two");
        int count = message.GetByteCount();
        Assert.IsFalse(message.TryWrite(new byte[count - 1], out int written));
        Assert.AreEqual(0, written);

        byte[] exact = new byte[count];
        Assert.IsTrue(message.TryWrite(exact, out written));
        Assert.AreEqual(count, written);
        CollectionAssert.AreEqual(message.ToArray(), exact);

        ArrayBufferWriter<byte> writer = new();
        Assert.AreEqual(count, message.Write(writer));
        CollectionAssert.AreEqual(exact, writer.WrittenSpan.ToArray());
    }

    [TestMethod]
    public void MessageWithoutArgumentsHasAnEmptyTypeTagString()
    {
        byte[] bytes = new OscMessage("/go").ToArray();
        Hex.AssertBytes("2f676f00 2c000000", bytes);
        OscMessage decoded = OscMessage.Parse(bytes);
        Assert.AreEqual(",", decoded.TypeTags);
        Assert.IsTrue(decoded.Arguments.IsEmpty);
        Assert.AreEqual("/go", decoded.ToString());
    }

    [TestMethod]
    public void AddressesAreValidated()
    {
        _ = Assert.ThrowsExactly<ArgumentException>(() => new OscMessage("no-slash"));
        _ = Assert.ThrowsExactly<ArgumentException>(() => new OscMessage("/tab\there"));
        _ = Assert.ThrowsExactly<ArgumentException>(() => new OscMessage(""));
        Assert.IsTrue(OscAddress.IsValid("/a/*/b"));
        Assert.IsTrue(OscAddress.IsPattern("/a/*/b"));
        Assert.IsFalse(OscAddress.IsPattern("/a/b"));
    }

    [TestMethod]
    public void ParseFromMemorySlicesBlobsInPlace()
    {
        byte[] packet = new OscMessage("/b", OscValue.FromBlob(new byte[] { 9, 8, 7 })).ToArray();
        OscMessage sliced = (OscMessage)OscPacket.Parse(packet.AsMemory());
        Assert.IsTrue(MemoryMarshal.TryGetArray(sliced.Arguments[0].AsBlob(), out ArraySegment<byte> segment));
        Assert.AreSame(packet, segment.Array);
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, sliced.Arguments[0].AsBlob().ToArray());

        OscMessage copied = (OscMessage)OscPacket.Parse(packet.AsSpan());
        Assert.IsTrue(MemoryMarshal.TryGetArray(copied.Arguments[0].AsBlob(), out segment));
        Assert.AreNotSame(packet, segment.Array);
    }

    [TestMethod]
    public void StreamingWriterProducesTheSameBytesAsTheModel()
    {
        OscMessage model = new("/stream", 7, 0.25f, "s", new byte[] { 0xAA });
        byte[] buffer = new byte[model.GetByteCount()];
        OscWriter writer = new(buffer);
        writer.WriteAddress("/stream");
        writer.WriteTypeTags("ifsb"u8);
        writer.WriteInt32(7);
        writer.WriteSingle(0.25f);
        writer.WriteString("s");
        writer.WriteBlob([0xAA]);
        Assert.AreEqual(buffer.Length, writer.BytesWritten);
        CollectionAssert.AreEqual(model.ToArray(), buffer);
    }

    [TestMethod]
    public void ReaderExposesRawFieldsWithoutAllocating()
    {
        byte[] packet = new OscMessage("/raw", 3, "txt").ToArray();
        OscReader reader = new(packet);
        Assert.IsTrue(reader.TryReadAddress(out ReadOnlySpan<byte> address));
        Assert.IsTrue(address.SequenceEqual("/raw"u8));
        Assert.IsTrue(reader.TryReadTypeTags(out ReadOnlySpan<byte> tags));
        Assert.IsTrue(tags.SequenceEqual("is"u8));
        Assert.IsTrue(reader.TryReadInt32(out int number));
        Assert.AreEqual(3, number);
        Assert.IsTrue(reader.TryReadString(out ReadOnlySpan<byte> text));
        Assert.IsTrue(text.SequenceEqual("txt"u8));
        Assert.AreEqual(0, reader.Remaining);
        Assert.AreEqual(OscParseError.None, reader.Error);
    }
}
