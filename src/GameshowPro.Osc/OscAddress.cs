namespace GameshowPro.Osc;

/// <summary>Validation of OSC address strings and address patterns.</summary>
public static class OscAddress
{
    private static readonly SearchValues<char> s_patternCharacters = SearchValues.Create("*?[]{}");

    /// <summary>
    /// True when <paramref name="address"/> can be sent: it starts with <c>/</c> and contains no control
    /// characters. Pattern characters are allowed, because a sender may legitimately address several
    /// methods at once. Non-ASCII characters are allowed and are encoded as UTF-8.
    /// </summary>
    public static bool IsValid(ReadOnlySpan<char> address)
    {
        if (address.IsEmpty || address[0] != '/')
        {
            return false;
        }
        foreach (char c in address)
        {
            if (c < ' ' || c == '\u007F')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>True when the address contains any OSC pattern character (<c>* ? [ ] { }</c>).</summary>
    public static bool IsPattern(ReadOnlySpan<char> address) => address.ContainsAny(s_patternCharacters);

    internal static string Validate(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return IsValid(address) ? address : throw new ArgumentException("An OSC address must start with '/' and contain no control characters.", nameof(address));
    }
}
