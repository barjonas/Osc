namespace GameshowPro.Osc.Tests;

[TestClass]
public sealed class OscStreamFramerTests
{
    [TestMethod]
    public void LengthPrefixedFramesRoundTripAndWaitForMoreData()
    {
        OscMessage first = new("/one", 1);
        OscMessage second = new("/two", "2");
        ArrayBufferWriter<byte> writer = new();
        int firstLength = OscStreamFramer.WriteFrame(writer, first, OscStreamFraming.LengthPrefixed);
        _ = OscStreamFramer.WriteFrame(writer, second, OscStreamFraming.LengthPrefixed);
        Assert.AreEqual(4 + first.GetByteCount(), firstLength);

        ReadOnlySpan<byte> stream = writer.WrittenSpan;
        Assert.IsTrue(OscStreamFramer.TryReadLengthPrefixedFrame(stream, out ReadOnlySpan<byte> packet, out int consumed));
        Assert.AreEqual(first, OscPacket.Parse(packet));
        Assert.AreEqual(firstLength, consumed);
        stream = stream[consumed..];
        Assert.IsTrue(OscStreamFramer.TryReadLengthPrefixedFrame(stream, out packet, out consumed));
        Assert.AreEqual(second, OscPacket.Parse(packet));
        Assert.AreEqual(stream.Length, consumed);

        Assert.IsFalse(OscStreamFramer.TryReadLengthPrefixedFrame(writer.WrittenSpan[..(firstLength - 1)], out _, out consumed));
        Assert.AreEqual(0, consumed);
        Assert.IsFalse(OscStreamFramer.TryReadLengthPrefixedFrame(writer.WrittenSpan[..3], out _, out _));
    }

    [TestMethod]
    public void LengthPrefixThatCannotBeAPacketThrows()
    {
        byte[] negative = [0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0];
        _ = Assert.ThrowsExactly<OscFormatException>(() => OscStreamFramer.TryReadLengthPrefixedFrame(negative, out _, out _));
        byte[] misaligned = [0, 0, 0, 9, 0, 0, 0, 0];
        _ = Assert.ThrowsExactly<OscFormatException>(() => OscStreamFramer.TryReadLengthPrefixedFrame(misaligned, out _, out _));
    }

    [TestMethod]
    public void SlipFramesEscapeSpecialBytesAndRoundTrip()
    {
        // Blob bytes include END (0xC0) and ESC (0xDB), which must be escaped on the wire.
        OscMessage message = new("/slip", OscValue.FromBlob(new byte[] { 0xC0, 0xDB, 0x01 }));
        ArrayBufferWriter<byte> writer = new();
        int written = OscStreamFramer.WriteFrame(writer, message, OscStreamFraming.Slip);

        ReadOnlySpan<byte> frame = writer.WrittenSpan;
        Assert.AreEqual(written, frame.Length);
        Assert.AreEqual(0xC0, frame[0]);
        Assert.AreEqual(0xC0, frame[^1]);
        Assert.AreEqual(message.GetByteCount() + 2 + 2, frame.Length);
        Assert.IsTrue(frame.Length <= OscStreamFramer.GetMaxSlipFrameLength(message.GetByteCount()));

        byte[] destination = new byte[frame.Length];
        Assert.IsTrue(OscStreamFramer.TryReadSlipFrame(frame, destination, out int packetLength, out int consumed));
        Assert.AreEqual(frame.Length, consumed);
        Assert.AreEqual(message, OscPacket.Parse(destination.AsSpan(0, packetLength)));

        Assert.IsFalse(OscStreamFramer.TryReadSlipFrame(frame[..^1], destination, out _, out consumed));
        Assert.AreEqual(0, consumed);
        _ = Assert.ThrowsExactly<OscFormatException>(() => OscStreamFramer.TryReadSlipFrame([0xC0, 0xDB, 0x00, 0xC0], new byte[4], out _, out _));
    }
}
