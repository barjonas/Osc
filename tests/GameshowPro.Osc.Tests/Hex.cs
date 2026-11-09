namespace GameshowPro.Osc.Tests;

internal static class Hex
{
    internal static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    internal static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    internal static void AssertBytes(string expectedHex, ReadOnlySpan<byte> actual)
        => Assert.AreEqual(Of(Bytes(expectedHex)), Of(actual));
}
