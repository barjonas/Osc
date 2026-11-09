namespace GameshowPro.Osc.Tests;

[TestClass]
public sealed class OscTimeTagTests
{
    [TestMethod]
    public void ImmediateIsOne()
    {
        Assert.AreEqual(1UL, OscTimeTag.Immediate.Value);
        Assert.IsTrue(OscTimeTag.Immediate.IsImmediate);
        Assert.IsFalse(new OscTimeTag(2).IsImmediate);
        Assert.AreEqual("immediate", OscTimeTag.Immediate.ToString());
    }

    [TestMethod]
    public void ConvertsToAndFromNtpTime()
    {
        DateTimeOffset y2k = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        OscTimeTag tag = OscTimeTag.FromDateTimeOffset(y2k);
        Assert.AreEqual(3_155_673_600U, tag.Seconds);
        Assert.AreEqual(0U, tag.Fraction);
        Assert.AreEqual(y2k, tag.ToDateTimeOffset());

        OscTimeTag half = OscTimeTag.FromDateTimeOffset(y2k.AddMilliseconds(500));
        Assert.AreEqual(0x8000_0000U, half.Fraction);
        Assert.AreEqual(y2k.AddMilliseconds(500), half.ToDateTimeOffset());

        Assert.AreEqual(0UL, OscTimeTag.FromDateTimeOffset(new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero)).Value);
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => OscTimeTag.FromDateTimeOffset(new DateTimeOffset(1899, 12, 31, 0, 0, 0, TimeSpan.Zero)));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => OscTimeTag.FromDateTimeOffset(new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [TestMethod]
    public void NowUsesTheTimeProvider()
    {
        DateTimeOffset fixedTime = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        OscTimeTag tag = OscTimeTag.Now(new FixedTimeProvider(fixedTime));
        Assert.AreEqual(fixedTime, tag.ToDateTimeOffset());
        Assert.IsTrue(tag > OscTimeTag.Immediate);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
