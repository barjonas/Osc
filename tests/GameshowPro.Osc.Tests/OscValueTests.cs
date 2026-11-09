namespace GameshowPro.Osc.Tests;

[TestClass]
public sealed class OscValueTests
{
    [TestMethod]
    public void DefaultIsNil()
    {
        OscValue value = default;
        Assert.AreEqual(OscTypeTag.Nil, value.Tag);
        Assert.IsTrue(value.IsNil);
        Assert.AreEqual(OscValue.Nil, value);
        Assert.IsNull(value.ToObject());
    }

    [TestMethod]
    public void ImplicitConversionsPickTheExpectedTag()
    {
        OscValue i = 1;
        OscValue h = 1L;
        OscValue f = 1f;
        OscValue d = 1d;
        OscValue c = 'x';
        OscValue s = "text";
        OscValue t = true;
        OscValue b = new byte[] { 1, 2 };
        OscValue tag = OscTimeTag.Immediate;
        OscValue color = new OscColor(1, 2, 3, 4);
        OscValue midi = new OscMidiMessage(0, 0x90, 60, 100);
        OscValue array = ImmutableArray.Create<OscValue>(1, 2);
        OscValue bits = ImmutableArray.Create(true, false);

        Assert.AreEqual(OscTypeTag.Int32, i.Tag);
        Assert.AreEqual(OscTypeTag.Int64, h.Tag);
        Assert.AreEqual(OscTypeTag.Float32, f.Tag);
        Assert.AreEqual(OscTypeTag.Double, d.Tag);
        Assert.AreEqual(OscTypeTag.Char, c.Tag);
        Assert.AreEqual(OscTypeTag.String, s.Tag);
        Assert.AreEqual(OscTypeTag.True, t.Tag);
        Assert.AreEqual(OscTypeTag.Blob, b.Tag);
        Assert.AreEqual(OscTypeTag.TimeTag, tag.Tag);
        Assert.AreEqual(OscTypeTag.Color, color.Tag);
        Assert.AreEqual(OscTypeTag.Midi, midi.Tag);
        Assert.AreEqual(OscTypeTag.ArrayBegin, array.Tag);
        Assert.AreEqual(OscTypeTag.BitVector, bits.Tag);
    }

    [TestMethod]
    public void AccessorsReturnTheStoredValueAndRejectOtherTypes()
    {
        OscValue value = OscValue.FromSingle(2.5f);
        Assert.AreEqual(2.5f, value.AsSingle());
        Assert.IsTrue(value.TryGetSingle(out float single));
        Assert.AreEqual(2.5f, single);
        Assert.IsFalse(value.TryGetInt32(out _));
        Assert.IsTrue(value.TryGetNumber(out double number));
        Assert.AreEqual(2.5, number);
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => value.AsInt32());
        Assert.AreEqual(2.5f, value.ToObject());

        Assert.AreEqual("text", OscValue.FromSymbol("text").AsString());
        Assert.AreEqual(OscTypeTag.Symbol, OscValue.FromSymbol("text").Tag);
        Assert.IsTrue(OscValue.FromInt64(long.MinValue).TryGetNumber(out double big));
        Assert.AreEqual(long.MinValue, big);
        Assert.IsFalse(OscValue.FromString("1").TryGetNumber(out _));
        Assert.IsTrue(OscValue.True.AsBoolean());
        Assert.IsFalse(OscValue.False.AsBoolean());
        Assert.AreEqual(float.PositiveInfinity, OscValue.Infinitum.ToObject());
    }

    [TestMethod]
    public void BlobReferencesTheCallersMemoryWithoutCopying()
    {
        byte[] backing = [0, 1, 2, 3, 4, 5, 6, 7];
        OscValue blob = OscValue.FromBlob(backing.AsMemory(2, 4));

        ReadOnlyMemory<byte> memory = blob.AsBlob();
        Assert.IsTrue(MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment));
        Assert.AreSame(backing, segment.Array);
        Assert.AreEqual(2, segment.Offset);
        Assert.AreEqual(4, segment.Count);
        CollectionAssert.AreEqual(new byte[] { 2, 3, 4, 5 }, memory.ToArray());
    }

    [TestMethod]
    public void EqualityIsWireEquality()
    {
        Assert.AreEqual(OscValue.FromInt32(1), OscValue.FromInt32(1));
        Assert.AreNotEqual(OscValue.FromInt32(1), OscValue.FromInt64(1));
        Assert.AreNotEqual(OscValue.FromInt32(1), OscValue.FromSingle(1));
        Assert.AreEqual(OscValue.FromSingle(float.NaN), OscValue.FromSingle(float.NaN));
        Assert.AreNotEqual(OscValue.FromDouble(0.0), OscValue.FromDouble(-0.0));
        Assert.AreEqual(OscValue.FromString("a"), OscValue.FromString("a"));
        Assert.AreNotEqual(OscValue.FromString("a"), OscValue.FromSymbol("a"));
        Assert.AreEqual(OscValue.FromBlob(new byte[] { 1, 2 }), OscValue.FromBlob(new byte[] { 1, 2 }.AsMemory()));
        Assert.AreEqual(OscValue.FromArray([1, "x"]), OscValue.FromArray([1, "x"]));
        Assert.AreNotEqual(OscValue.FromArray([1, "x"]), OscValue.FromArray([1, "y"]));
        Assert.AreEqual(OscValue.FromBitVector([true, false]), OscValue.FromBitVector([true, false]));
        Assert.AreEqual(OscValue.FromInt32(7).GetHashCode(), OscValue.FromInt32(7).GetHashCode());
        Assert.AreEqual(OscValue.FromArray([1, 2]).GetHashCode(), OscValue.FromArray([1, 2]).GetHashCode());
    }

    [TestMethod]
    public void StringsCannotContainNull()
    {
        _ = Assert.ThrowsExactly<ArgumentException>(() => OscValue.FromString("a\0b"));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => OscValue.FromString(null!));
    }

    [TestMethod]
    public void ToStringDescribesEachType()
    {
        Assert.AreEqual("42", OscValue.FromInt32(42).ToString());
        Assert.AreEqual("\"hi\"", OscValue.FromString("hi").ToString());
        Assert.AreEqual("[1, 2.5f]", OscValue.FromArray([1, 2.5f]).ToString());
        Assert.AreEqual("bits[101]", OscValue.FromBitVector([true, false, true]).ToString());
        Assert.AreEqual("nil", OscValue.Nil.ToString());
        Assert.AreEqual("#01020304", OscValue.FromColor(new(1, 2, 3, 4)).ToString());
    }
}
