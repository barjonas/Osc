namespace GameshowPro.Osc.Tests;

[TestClass]
public sealed class OscMalformedPacketTests
{
    private static OscParseError ErrorOf(byte[] packet, OscReaderOptions? options = null)
    {
        Assert.IsFalse(OscPacket.TryParse(packet, out OscPacket? parsed, out OscParseError error, options));
        Assert.IsNull(parsed);
        Assert.AreNotEqual(OscParseError.None, error);
        return error;
    }

    [TestMethod]
    public void RejectsTooShortAndMisalignedPackets()
    {
        Assert.AreEqual(OscParseError.EmptyPacket, ErrorOf([]));
        Assert.AreEqual(OscParseError.EmptyPacket, ErrorOf("/a\0\0"u8.ToArray()));
        Assert.AreEqual(OscParseError.InvalidLength, ErrorOf([.. new OscMessage("/a").ToArray(), 0]));
    }

    [TestMethod]
    public void RejectsBadAddresses()
    {
        Assert.AreEqual(OscParseError.InvalidAddress, ErrorOf("abcd\0\0\0\0,\0\0\0"u8.ToArray()));
        Assert.AreEqual(OscParseError.InvalidAddress, ErrorOf("/a\tb\0\0\0\0,\0\0\0"u8.ToArray()));
        Assert.AreEqual(OscParseError.InvalidAddress, ErrorOf([(byte)'/', 0xFF, 0, 0, (byte)',', 0, 0, 0]));
    }

    [TestMethod]
    public void RejectsMissingOrUnknownTypeTags()
    {
        Assert.AreEqual(OscParseError.MissingTypeTags, ErrorOf("/a\0\0i\0\0\0"u8.ToArray()));
        Assert.AreEqual(OscParseError.UnsupportedTypeTag, ErrorOf("/a\0\0,z\0\0"u8.ToArray()));
        Assert.AreEqual(OscParseError.UnexpectedEnd, ErrorOf("/a\0\0"u8.ToArray().Concat("\0\0\0\0"u8.ToArray()).ToArray()[..4].Concat(",i\0\0"u8.ToArray()).ToArray()));
    }

    [TestMethod]
    public void RejectsTruncatedArguments()
    {
        byte[] valid = new OscMessage("/a", 1, "str").ToArray();
        Assert.AreEqual(OscParseError.UnexpectedEnd, ErrorOf(valid[..^4]));
        Assert.AreEqual(OscParseError.UnexpectedEnd, ErrorOf(new OscMessage("/a", 1L).ToArray()[..^4]));
    }

    [TestMethod]
    public void RejectsBadPaddingAndTrailingData()
    {
        byte[] badPadding = new OscMessage("/a", "s").ToArray();
        badPadding[^1] = 1;
        Assert.AreEqual(OscParseError.InvalidPadding, ErrorOf(badPadding));

        // The address swallows ",i" because the first terminator is at the end, so the type tags run out of bytes.
        byte[] noTerminator = "/abc,i\0\0"u8.ToArray();
        Assert.AreEqual(OscParseError.UnexpectedEnd, ErrorOf(noTerminator));
        Assert.AreEqual(OscParseError.UnexpectedEnd, ErrorOf("garbage!"u8.ToArray()));

        Assert.AreEqual(OscParseError.TrailingData, ErrorOf([.. new OscMessage("/a").ToArray(), 0, 0, 0, 0]));
    }

    [TestMethod]
    public void RejectsBadBlobsAndStrings()
    {
        byte[] blob = new OscMessage("/a", OscValue.FromBlob(new byte[] { 1, 2 })).ToArray();
        BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(8), -1);
        Assert.AreEqual(OscParseError.InvalidBlobLength, ErrorOf(blob));
        BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(8), 100);
        Assert.AreEqual(OscParseError.InvalidBlobLength, ErrorOf(blob));

        byte[] invalidUtf8 = new OscMessage("/a", "ab").ToArray();
        invalidUtf8[^4] = 0xC3;
        Assert.AreEqual(OscParseError.InvalidString, ErrorOf(invalidUtf8));
    }

    [TestMethod]
    public void RejectsUnbalancedArrays()
    {
        Assert.AreEqual(OscParseError.InvalidArray, ErrorOf("/a\0\0,[i\0"u8.ToArray().Concat(new byte[] { 0, 0, 0, 1 }).ToArray()));
        Assert.AreEqual(OscParseError.InvalidArray, ErrorOf("/a\0\0,]\0\0"u8.ToArray()));
    }

    [TestMethod]
    public void RejectsBadBundles()
    {
        byte[] bundle = new OscBundle(new OscMessage("/a")).ToArray();
        Assert.AreEqual(OscParseError.InvalidElementSize, ErrorOf(bundle[..^4]));

        byte[] oddSize = (byte[])bundle.Clone();
        BinaryPrimitives.WriteInt32BigEndian(oddSize.AsSpan(16), 7);
        Assert.AreEqual(OscParseError.InvalidElementSize, ErrorOf(oddSize));

        byte[] badHeader = (byte[])bundle.Clone();
        badHeader[1] = (byte)'B';
        Assert.AreEqual(OscParseError.InvalidAddress, ErrorOf(badHeader));

        Assert.AreEqual(OscParseError.InvalidBundleHeader, ErrorOf(bundle[..12]));
        Assert.AreEqual(OscParseError.EmptyPacket, ErrorOf(bundle[..4]));
    }

    [TestMethod]
    public void EnforcesDepthAndElementLimits()
    {
        OscPacket deep = new OscMessage("/leaf");
        for (int i = 0; i < 3; i++)
        {
            deep = new OscBundle(deep);
        }
        // Three bundle levels wrap the message, so the nesting depth is 3.
        byte[] bytes = deep.ToArray();
        Assert.IsTrue(OscPacket.TryParse(bytes, out _, out _, new OscReaderOptions { MaxBundleDepth = 3 }));
        Assert.AreEqual(OscParseError.BundleTooDeep, ErrorOf(bytes, new OscReaderOptions { MaxBundleDepth = 2 }));

        // Five messages in a bundle are five elements; the outer bundle itself is not counted.
        byte[] wide = new OscBundle([.. Enumerable.Range(0, 5).Select(_ => (OscPacket)new OscMessage("/m"))]).ToArray();
        Assert.IsTrue(OscPacket.TryParse(wide, out _, out _, new OscReaderOptions { MaxBundleElements = 5 }));
        Assert.AreEqual(OscParseError.TooManyElements, ErrorOf(wide, new OscReaderOptions { MaxBundleElements = 4 }));
        byte[] nestedWide = new OscBundle(new OscBundle(new OscMessage("/a"), new OscMessage("/b")), new OscMessage("/c")).ToArray();
        Assert.IsTrue(OscPacket.TryParse(nestedWide, out _, out _, new OscReaderOptions { MaxBundleElements = 4 }));
        Assert.AreEqual(OscParseError.TooManyElements, ErrorOf(nestedWide, new OscReaderOptions { MaxBundleElements = 3 }));
    }

    [TestMethod]
    public void ThrowingParseReportsTheError()
    {
        OscFormatException exception = Assert.ThrowsExactly<OscFormatException>(() => OscPacket.Parse("/a\0\0,z\0\0"u8));
        Assert.AreEqual(OscParseError.UnsupportedTypeTag, exception.Error);
    }
}
