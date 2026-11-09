namespace GameshowPro.Osc.Tests;

[TestClass]
public sealed class OscRelaxedValidationTests
{
    private static readonly OscReaderOptions s_acceptOpaque = new() { UnknownTypeTags = OscUnknownTypeTagHandling.AcceptOpaque };

    /// <summary><c>/a ,izf</c> where <c>z</c> is unknown: one int32, then eight bytes the reader cannot interpret.</summary>
    private static readonly byte[] s_unknownTail =
        [.. "/a\0\0"u8, .. ",izf\0\0\0\0"u8, 0, 0, 0, 1, 0xDE, 0xAD, 0xBE, 0xEF, 0x3F, 0x80, 0x00, 0x00];

    [TestMethod]
    public void UnknownTagIsRejectedByDefault()
    {
        Assert.IsFalse(OscPacket.TryParse(s_unknownTail, out OscPacket? packet, out OscParseError error));
        Assert.IsNull(packet);
        Assert.AreEqual(OscParseError.UnsupportedTypeTag, error);
        Assert.AreEqual(OscUnknownTypeTagHandling.Reject, OscReaderOptions.Default.UnknownTypeTags);
    }

    [TestMethod]
    public void AcceptOpaqueKeepsTheTailAndRoundTrips()
    {
        Assert.IsTrue(OscPacket.TryParse(s_unknownTail, out OscPacket? packet, out OscParseError error, s_acceptOpaque));
        Assert.AreEqual(OscParseError.None, error);
        OscMessage message = Assert.IsInstanceOfType<OscMessage>(packet);

        Assert.AreEqual("/a", message.Address);
        Assert.HasCount(1, message.Arguments);
        Assert.AreEqual(1, message.Arguments[0].AsInt32());
        Assert.AreEqual("zf", message.OpaqueTypeTags);
        Hex.AssertBytes("DEADBEEF 3F800000", message.OpaqueArguments.Span);
        Assert.AreEqual(",izf", message.TypeTags);
        Assert.AreEqual(s_unknownTail.Length, message.GetByteCount());
        CollectionAssert.AreEqual(s_unknownTail, message.ToArray());
        Assert.AreEqual("/a ,izf 1 [8 opaque bytes]", message.ToString());
    }

    [TestMethod]
    public void OpaqueTailSlicesMemoryButCopiesFromASpan()
    {
        byte[] source = (byte[])s_unknownTail.Clone();
        OscMessage fromMemory = (OscMessage)OscPacket.Parse(source.AsMemory(), s_acceptOpaque);
        Assert.IsTrue(MemoryMarshal.TryGetArray(fromMemory.OpaqueArguments, out ArraySegment<byte> segment));
        Assert.AreSame(source, segment.Array);
        Assert.AreEqual(source.Length - 8, segment.Offset);

        OscMessage fromSpan = (OscMessage)OscPacket.Parse(source.AsSpan(), s_acceptOpaque);
        Assert.IsTrue(MemoryMarshal.TryGetArray(fromSpan.OpaqueArguments, out segment));
        Assert.AreNotSame(source, segment.Array);

        Assert.AreEqual(fromMemory, fromSpan);
        Assert.AreEqual(fromMemory.GetHashCode(), fromSpan.GetHashCode());
        Assert.AreNotEqual(fromMemory, new OscMessage("/a", 1));
    }

    [TestMethod]
    public void OpaqueTailMayBeEmptyAndMayStartAtTheFirstTag()
    {
        byte[] noData = [.. "/a\0\0"u8, .. ",iz\0"u8, 0, 0, 0, 7];
        OscMessage message = OscMessage.Parse(noData, s_acceptOpaque);
        Assert.AreEqual("z", message.OpaqueTypeTags);
        Assert.IsTrue(message.OpaqueArguments.IsEmpty);
        CollectionAssert.AreEqual(noData, message.ToArray());

        byte[] onlyUnknown = [.. "/a\0\0"u8, .. ",zz\0"u8, 1, 2, 3, 4];
        message = OscMessage.Parse(onlyUnknown, s_acceptOpaque);
        Assert.IsEmpty(message.Arguments);
        Assert.AreEqual("zz", message.OpaqueTypeTags);
        Assert.AreEqual(",zz", message.TypeTags);
        Assert.AreEqual("/a ,zz [4 opaque bytes]", message.ToString());
        CollectionAssert.AreEqual(onlyUnknown, message.ToArray());

        OscMessage ordinary = new("/a", 1);
        Assert.IsNull(ordinary.OpaqueTypeTags);
        Assert.IsTrue(ordinary.OpaqueArguments.IsEmpty);
    }

    [TestMethod]
    public void UnknownTagInsideAnArrayIsStillRejected()
    {
        byte[] standard = [.. "/a\0\0"u8, .. ",[z]\0\0\0\0"u8, 0, 0, 0, 1];
        Assert.IsFalse(OscPacket.TryParse(standard, out _, out OscParseError error, s_acceptOpaque));
        Assert.AreEqual(OscParseError.UnsupportedTypeTag, error);

        OscReaderOptions ventuz = s_acceptOpaque with { ArrayEncoding = OscArrayEncoding.VentuzRemoting };
        byte[] remoting = [.. "/a\0\0"u8, .. ",[z]\0\0\0\0"u8, 0, 0, 0, 4, 0, 0, 0, 1, 0, 0, 0, 1];
        Assert.IsFalse(OscPacket.TryParse(remoting, out _, out error, ventuz));
        Assert.AreEqual(OscParseError.UnsupportedTypeTag, error);
    }

    [TestMethod]
    public void BundleWithAnUnknownTagMessageFollowsTheOption()
    {
        OscMessage opaque = OscMessage.Parse(s_unknownTail, s_acceptOpaque);
        byte[] bundle = new OscBundle(new OscMessage("/ok", 1), opaque).ToArray();

        Assert.IsFalse(OscPacket.TryParse(bundle, out _, out OscParseError parseError));
        Assert.AreEqual(OscParseError.UnsupportedTypeTag, parseError);
        Assert.IsFalse(OscPacket.TryValidate(bundle, out OscParseError validateError));
        Assert.AreEqual(OscParseError.UnsupportedTypeTag, validateError);

        Assert.IsTrue(OscPacket.TryParse(bundle, out OscPacket? packet, out parseError, s_acceptOpaque));
        Assert.AreEqual(OscParseError.None, parseError);
        Assert.IsTrue(OscPacket.TryValidate(bundle, out validateError, s_acceptOpaque));
        Assert.AreEqual(OscParseError.None, validateError);

        OscBundle parsed = Assert.IsInstanceOfType<OscBundle>(packet);
        Assert.AreEqual(opaque, parsed.Elements[1]);
        CollectionAssert.AreEqual(bundle, parsed.ToArray());
    }

    [TestMethod]
    public void ReaderReportsWhereDecodingStopped()
    {
        OscReader reader = new(s_unknownTail, s_acceptOpaque);
        Assert.IsTrue(reader.TryReadAddress(out _));
        Assert.IsTrue(reader.TryReadTypeTags(out ReadOnlySpan<byte> typeTags));
        Assert.IsTrue(reader.TryReadArguments(typeTags, out ImmutableArray<OscValue> arguments, out ReadOnlySpan<byte> opaqueTypeTags));
        Assert.HasCount(1, arguments);
        Assert.IsTrue(opaqueTypeTags.SequenceEqual("zf"u8));
        Assert.AreEqual(8, reader.Remaining);

        // A single value has no tail to keep, so the per-value reader still reports the unknown tag.
        OscReader single = new(s_unknownTail, s_acceptOpaque);
        Assert.IsTrue(single.TryReadAddress(out _));
        Assert.IsTrue(single.TryReadTypeTags(out typeTags));
        int index = 1;
        Assert.IsFalse(single.TryReadValue(typeTags, ref index, out _));
        Assert.AreEqual(OscParseError.UnsupportedTypeTag, single.Error);
    }

    [TestMethod]
    public void TryValidateAgreesWithTryParse()
    {
        foreach ((string name, byte[] packet, OscReaderOptions? options) in Vectors())
        {
            bool parsed = OscPacket.TryParse(packet, out _, out OscParseError parseError, options);
            bool validated = OscPacket.TryValidate(packet, out OscParseError validateError, options);
            Assert.AreEqual(parsed, validated, $"{name}: result");
            Assert.AreEqual(parseError, validateError, $"{name}: error");
        }
    }

    /// <summary>
    /// Every malformed vector from <see cref="OscMalformedPacketTests"/>, the valid packets they are cut from, and
    /// the opaque cases above. <see cref="OscMalformedPacketTests"/> also checks <c>TryValidate</c> on each of its
    /// vectors as it runs, so a vector added there is covered even if this table is not updated.
    /// </summary>
    private static IEnumerable<(string Name, byte[] Packet, OscReaderOptions? Options)> Vectors()
    {
        OscReaderOptions ventuz = new() { ArrayEncoding = OscArrayEncoding.VentuzRemoting };

        // Valid.
        yield return ("empty args", new OscMessage("/a").ToArray(), null);
        yield return ("every type", new OscMessage("/a", 1, 0.5f, "s", OscValue.FromBlob(new byte[] { 1, 2, 3 }), 1L, OscTimeTag.Immediate, 2.5,
            OscValue.FromSymbol("S"), 'c', new OscColor(1, 2, 3, 4), new OscMidiMessage(1, 2, 3, 4), true, false, OscValue.Nil, OscValue.Infinitum,
            OscValue.FromArray([1, OscValue.FromArray(["x", 2.0])]), OscValue.FromBitVector([true, false, true])).ToArray(), null);
        yield return ("ventuz array", new OscMessage("/a", OscValue.FromArray([1, 2, 3])).ToArray(OscWriterOptions.VentuzRemoting), ventuz);
        yield return ("nested bundle", new OscBundle(new OscMessage("/a"), new OscBundle(new OscMessage("/b", 2))).ToArray(), null);
        yield return ("opaque tail", s_unknownTail, s_acceptOpaque);
        yield return ("opaque bundle", new OscBundle(new OscMessage("/ok", 1), OscMessage.Parse(s_unknownTail, s_acceptOpaque)).ToArray(), s_acceptOpaque);

        // Length and alignment.
        yield return ("empty", [], null);
        yield return ("too short", "/a\0\0"u8.ToArray(), null);
        yield return ("misaligned", [.. new OscMessage("/a").ToArray(), 0], null);

        // Addresses.
        yield return ("no slash", "abcd\0\0\0\0,\0\0\0"u8.ToArray(), null);
        yield return ("control char", "/a\tb\0\0\0\0,\0\0\0"u8.ToArray(), null);
        yield return ("invalid utf8 address", [(byte)'/', 0xFF, 0, 0, (byte)',', 0, 0, 0], null);

        // Type tags.
        yield return ("missing tags", "/a\0\0i\0\0\0"u8.ToArray(), null);
        yield return ("unknown tag", "/a\0\0,z\0\0"u8.ToArray(), null);
        yield return ("unknown tag accepted", "/a\0\0,z\0\0"u8.ToArray(), s_acceptOpaque);
        yield return ("tags cut", [.. "/a\0\0"u8, .. ",i\0\0"u8], null);

        // Truncated arguments.
        byte[] valid = new OscMessage("/a", 1, "str").ToArray();
        yield return ("truncated string", valid[..^4], null);
        yield return ("truncated int64", new OscMessage("/a", 1L).ToArray()[..^4], null);

        // Padding and trailing data.
        byte[] badPadding = new OscMessage("/a", "s").ToArray();
        badPadding[^1] = 1;
        yield return ("bad padding", badPadding, null);
        yield return ("no terminator", "/abc,i\0\0"u8.ToArray(), null);
        yield return ("garbage", "garbage!"u8.ToArray(), null);
        yield return ("trailing data", [.. new OscMessage("/a").ToArray(), 0, 0, 0, 0], null);

        // Blobs and strings.
        byte[] negativeBlob = new OscMessage("/a", OscValue.FromBlob(new byte[] { 1, 2 })).ToArray();
        BinaryPrimitives.WriteInt32BigEndian(negativeBlob.AsSpan(8), -1);
        yield return ("negative blob", negativeBlob, null);
        byte[] longBlob = (byte[])negativeBlob.Clone();
        BinaryPrimitives.WriteInt32BigEndian(longBlob.AsSpan(8), 100);
        yield return ("blob too long", longBlob, null);
        byte[] invalidUtf8 = new OscMessage("/a", "ab").ToArray();
        invalidUtf8[^4] = 0xC3;
        yield return ("invalid utf8 string", invalidUtf8, null);

        // Arrays.
        yield return ("unclosed array", [.. "/a\0\0,[i\0"u8, 0, 0, 0, 1], null);
        yield return ("stray close", "/a\0\0,]\0\0"u8.ToArray(), null);
        yield return ("unknown in array", [.. "/a\0\0"u8, .. ",[z]\0\0\0\0"u8, 0, 0, 0, 1], s_acceptOpaque);
        yield return ("unknown in ventuz array", [.. "/a\0\0"u8, .. ",[z]\0\0\0\0"u8, 0, 0, 0, 4, 0, 0, 0, 1, 0, 0, 0, 1],
            s_acceptOpaque with { ArrayEncoding = OscArrayEncoding.VentuzRemoting });
        yield return ("ventuz nested", [.. "/a\0\0"u8, .. ",[[]\0\0\0\0"u8, 0, 0, 0, 0, 0, 0, 0, 0], ventuz);
        yield return ("ventuz count too large", [.. "/a\0\0"u8, .. ",[i]\0\0\0\0"u8, 0, 0, 0, 4, 0, 0, 0, 9, 0, 0, 0, 1], ventuz);

        // Bundles.
        byte[] bundle = new OscBundle(new OscMessage("/a")).ToArray();
        yield return ("bundle cut", bundle[..^4], null);
        byte[] oddSize = (byte[])bundle.Clone();
        BinaryPrimitives.WriteInt32BigEndian(oddSize.AsSpan(16), 7);
        yield return ("odd element size", oddSize, null);
        byte[] badHeader = (byte[])bundle.Clone();
        badHeader[1] = (byte)'B';
        yield return ("bad bundle header", badHeader, null);
        yield return ("header only", bundle[..12], null);
        yield return ("prefix only", bundle[..4], null);

        // Limits.
        OscPacket deep = new OscMessage("/leaf");
        for (int i = 0; i < 3; i++)
        {
            deep = new OscBundle(deep);
        }
        yield return ("depth ok", deep.ToArray(), new OscReaderOptions { MaxBundleDepth = 3 });
        yield return ("too deep", deep.ToArray(), new OscReaderOptions { MaxBundleDepth = 2 });
        byte[] wide = new OscBundle([.. Enumerable.Range(0, 5).Select(_ => (OscPacket)new OscMessage("/m"))]).ToArray();
        yield return ("elements ok", wide, new OscReaderOptions { MaxBundleElements = 5 });
        yield return ("too many elements", wide, new OscReaderOptions { MaxBundleElements = 4 });
        byte[] nestedWide = new OscBundle(new OscBundle(new OscMessage("/a"), new OscMessage("/b")), new OscMessage("/c")).ToArray();
        yield return ("nested elements ok", nestedWide, new OscReaderOptions { MaxBundleElements = 4 });
        yield return ("nested too many", nestedWide, new OscReaderOptions { MaxBundleElements = 3 });
    }
}
