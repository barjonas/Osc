namespace GameshowPro.Osc.Tests;

/// <summary>Conventions of Ventuz.OSC.dll (the scene nodes), established from the shipped Ventuz 8 assembly.</summary>
[TestClass]
public sealed class OscVentuzNodeTests
{
    [TestMethod]
    public void UnicodeStringIsAMarkedBigEndianUtf16Blob()
    {
        OscValue value = OscVentuz.UnicodeString("é✓");

        Assert.AreEqual(OscTypeTag.Blob, value.Tag);
        Hex.AssertBytes("42455543 00e9 2713", value.AsBlob().Span);
        Assert.IsTrue(OscVentuz.IsUnicodeString(value));
        Assert.IsTrue(OscVentuz.TryGetUnicodeString(value, out string? text));
        Assert.AreEqual("é✓", text);

        OscMessage decoded = OscMessage.Parse(new OscMessage("/text", value).ToArray(OscWriterOptions.VentuzNode));
        Assert.AreEqual(",b", decoded.TypeTags);
        Assert.IsTrue(OscVentuz.TryGetUnicodeString(decoded.Arguments[0], out text));
        Assert.AreEqual("é✓", text);

        Hex.AssertBytes("42455543", OscVentuz.UnicodeString("").AsBlob().Span);
        Assert.IsTrue(OscVentuz.TryGetUnicodeString(OscVentuz.UnicodeString(""), out text));
        Assert.AreEqual("", text);
    }

    [TestMethod]
    public void PlainBlobsAndOtherTypesAreNotMistakenForVentuzExtensions()
    {
        Assert.IsFalse(OscVentuz.IsUnicodeString(OscValue.FromBlob(new byte[] { 0x42, 0x45, 0x55 })));
        Assert.IsFalse(OscVentuz.TryGetUnicodeString(OscValue.FromBlob("BEUX"u8.ToArray()), out _));
        Assert.IsFalse(OscVentuz.TryGetUnicodeString(OscValue.FromString("BEUC"), out _));
        Assert.IsFalse(OscVentuz.IsImage(OscValue.FromBlob(ReadOnlyMemory<byte>.Empty)));
        Assert.IsFalse(OscVentuz.TryGetImage(OscValue.Nil, out _));
        Assert.IsFalse(OscVentuz.TryGetColor(OscValue.FromInt32(1), out _));
    }

    [TestMethod]
    public void ImageIsAMarkedPngBlobReturnedInPlace()
    {
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        OscValue value = OscVentuz.Image(png);

        Hex.AssertBytes("504e4749 89504e470d0a1a0a010203", value.AsBlob().Span);
        Assert.IsTrue(OscVentuz.IsImage(value));
        Assert.IsTrue(OscVentuz.TryGetImage(value, out ReadOnlyMemory<byte> back));
        CollectionAssert.AreEqual(png, back.ToArray());
        Assert.IsTrue(MemoryMarshal.TryGetArray(back, out ArraySegment<byte> segment));
        Assert.AreEqual(4, segment.Offset);
    }

    [TestMethod]
    public void ColoursUseArgbOrderOnTheWire()
    {
        OscColor color = new(0x11, 0x22, 0x33, 0x44);
        OscValue value = OscVentuz.Color(color);

        byte[] packet = new OscMessage("/c", value).ToArray();
        // Address "/c\0\0", tags ",r\0\0", then A R G B as Color.ToArgb() writes them.
        Hex.AssertBytes("2f630000 2c720000 44112233", packet);
        Assert.IsTrue(OscVentuz.TryGetColor(OscMessage.Parse(packet).Arguments[0], out OscColor back));
        Assert.AreEqual(color, back);

        Assert.AreEqual(unchecked((int)0x44112233), color.ToArgb());
        Assert.AreEqual(color, OscColor.FromArgb(color.ToArgb()));
        Assert.AreEqual(0x11223344U, color.ToPacked());
    }

    [TestMethod]
    public void UpperCaseMidiTagIsRead()
    {
        byte[] packet = new OscMessage("/m", new OscMidiMessage(1, 0x90, 60, 100)).ToArray();
        Assert.AreEqual((byte)'m', packet[5]);
        packet[5] = (byte)'M';

        OscMessage decoded = OscMessage.Parse(packet);
        Assert.AreEqual(new OscMidiMessage(1, 0x90, 60, 100), decoded.Arguments[0].AsMidi());
        Assert.AreEqual(",m", decoded.TypeTags);
    }

    [TestMethod]
    public void VentuzNodeOptionsRejectNonAsciiStrings()
    {
        _ = Assert.ThrowsExactly<OscFormatException>(() => new OscMessage("/s", "é").ToArray(OscWriterOptions.VentuzNode));
        Hex.AssertBytes("2f730000 2c730000 61000000", new OscMessage("/s", "a").ToArray(OscWriterOptions.VentuzNode));
        Assert.AreEqual(OscArrayEncoding.Standard, OscWriterOptions.VentuzNode.ArrayEncoding);
        Assert.AreEqual(OscArrayEncoding.VentuzRemoting, OscWriterOptions.VentuzRemoting.ArrayEncoding);
    }

    [TestMethod]
    public void UnsignedLongTravelsAsATimeTagForVentuz()
    {
        // Ventuz.OSC maps System.UInt64 to 't'; this library's OscTimeTag is the same 64-bit word.
        OscMessage message = new("/u", new OscTimeTag(0xFFFF_FFFF_FFFF_FFFEUL));
        Assert.AreEqual(",t", message.TypeTags);
        Assert.AreEqual(0xFFFF_FFFF_FFFF_FFFEUL, OscMessage.Parse(message.ToArray()).Arguments[0].AsTimeTag().Value);
    }
}
