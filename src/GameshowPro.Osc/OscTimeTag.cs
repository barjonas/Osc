namespace GameshowPro.Osc;

/// <summary>
/// A 64-bit OSC time tag in NTP format: the high 32 bits count seconds since 1900-01-01T00:00:00Z and the
/// low 32 bits are the fraction of a second. The value 1 means "immediately".
/// </summary>
public readonly record struct OscTimeTag(ulong Value) : IComparable<OscTimeTag>
{
    private static readonly DateTimeOffset s_epoch = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    /// <summary>The special value that asks the receiver to act as soon as the packet arrives.</summary>
    public static OscTimeTag Immediate { get; } = new(1);

    public bool IsImmediate => Value == 1;

    /// <summary>Whole seconds since the NTP epoch.</summary>
    public uint Seconds => (uint)(Value >> 32);

    /// <summary>Fraction of a second in units of 2^-32 s.</summary>
    public uint Fraction => (uint)Value;

    public OscTimeTag(uint seconds, uint fraction) : this(((ulong)seconds << 32) | fraction)
    {
    }

    /// <summary>Converts a point in time to a time tag. Throws when outside the representable 1900–2036 range.</summary>
    public static OscTimeTag FromDateTimeOffset(DateTimeOffset time)
    {
        long ticks = (time - s_epoch).Ticks;
        if (ticks < 0 || ticks / TicksPerSecond > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(time), time, "The time is outside the range an OSC time tag can represent.");
        }
        uint seconds = (uint)(ticks / TicksPerSecond);
        // (remainder * 2^32) / TicksPerSecond: the remainder is below 10^7, so the product fits in a long.
        uint fraction = (uint)(((ticks % TicksPerSecond) << 32) / TicksPerSecond);
        return new(seconds, fraction);
    }

    /// <summary>The current time as a time tag, from <paramref name="timeProvider"/> or the system clock.</summary>
    public static OscTimeTag Now(TimeProvider? timeProvider = null)
        => FromDateTimeOffset((timeProvider ?? TimeProvider.System).GetUtcNow());

    /// <summary>
    /// Converts the time tag to a point in time. <see cref="Immediate"/> has no calendar meaning and
    /// converts to the epoch plus one fraction unit, so check <see cref="IsImmediate"/> first.
    /// </summary>
    public DateTimeOffset ToDateTimeOffset()
    {
        long ticks = (Seconds * TicksPerSecond) + (long)(((ulong)Fraction * TicksPerSecond) >> 32);
        return s_epoch.AddTicks(ticks);
    }

    public int CompareTo(OscTimeTag other) => Value.CompareTo(other.Value);

    public static bool operator <(OscTimeTag left, OscTimeTag right) => left.Value < right.Value;
    public static bool operator >(OscTimeTag left, OscTimeTag right) => left.Value > right.Value;
    public static bool operator <=(OscTimeTag left, OscTimeTag right) => left.Value <= right.Value;
    public static bool operator >=(OscTimeTag left, OscTimeTag right) => left.Value >= right.Value;

    public override string ToString() => IsImmediate ? "immediate" : ToDateTimeOffset().ToString("O", CultureInfo.InvariantCulture);
}
