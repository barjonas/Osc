namespace GameshowPro.Osc;

/// <summary>A 32-bit RGBA colour, the OSC 1.1 <c>r</c> type. On the wire the bytes are R, G, B, A in that order.</summary>
public readonly record struct OscColor(byte R, byte G, byte B, byte A)
{
    /// <summary>The colour packed as the big-endian 32-bit word that appears on the wire: <c>0xRRGGBBAA</c>.</summary>
    public uint ToPacked() => ((uint)R << 24) | ((uint)G << 16) | ((uint)B << 8) | A;

    public static OscColor FromPacked(uint packed)
        => new((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);

    /// <summary>The colour as a Windows ARGB word (<c>0xAARRGGBB</c>), as <c>System.Drawing.Color.ToArgb()</c> returns. Not the wire order; see <see cref="OscVentuz.Color"/>.</summary>
    public int ToArgb() => unchecked((int)(((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B));

    public static OscColor FromArgb(int argb)
        => new((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));

    public override string ToString() => $"#{ToPacked():X8}";
}
