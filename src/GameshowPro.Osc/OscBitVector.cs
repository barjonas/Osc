namespace GameshowPro.Osc;

/// <summary>
/// The Ventuz <c>B</c> encoding: bits packed LSB-first, eight per byte, followed by a single sentinel 1 bit,
/// then zero padding to the byte. The blob is therefore <c>(bitCount / 8) + 1</c> bytes long and the sentinel
/// marks where the data ends. A final byte of zero is also accepted and means no bits are carried in it.
/// </summary>
internal static class OscBitVector
{
    internal static int EncodedLength(int bitCount) => (bitCount >> 3) + 1;

    internal static void Encode(ReadOnlySpan<bool> bits, Span<byte> destination)
    {
        destination = destination[..EncodedLength(bits.Length)];
        destination.Clear();
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                destination[i >> 3] |= (byte)(1 << (i & 7));
            }
        }
        destination[bits.Length >> 3] |= (byte)(1 << (bits.Length & 7));
    }

    internal static bool TryDecode(ReadOnlySpan<byte> blob, out ImmutableArray<bool> bits)
    {
        if (blob.IsEmpty)
        {
            bits = [];
            return true;
        }
        byte last = blob[^1];
        int bitCount = ((blob.Length - 1) * 8) + (last == 0 ? 0 : BitOperations.Log2(last));
        bool[] result = new bool[bitCount];
        for (int i = 0; i < bitCount; i++)
        {
            result[i] = (blob[i >> 3] & (1 << (i & 7))) != 0;
        }
        bits = ImmutableCollectionsMarshal.AsImmutableArray(result);
        return true;
    }
}
