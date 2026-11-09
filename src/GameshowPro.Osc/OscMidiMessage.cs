namespace GameshowPro.Osc;

/// <summary>A 4-byte MIDI message, the OSC 1.1 <c>m</c> type: port id, status byte, data 1, data 2.</summary>
public readonly record struct OscMidiMessage(byte PortId, byte Status, byte Data1, byte Data2)
{
    /// <summary>The message packed as the big-endian 32-bit word that appears on the wire.</summary>
    public uint ToPacked() => ((uint)PortId << 24) | ((uint)Status << 16) | ((uint)Data1 << 8) | Data2;

    public static OscMidiMessage FromPacked(uint packed)
        => new((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);

    public override string ToString() => $"midi {PortId:X2} {Status:X2} {Data1:X2} {Data2:X2}";
}
