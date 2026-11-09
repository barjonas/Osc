namespace GameshowPro.Osc;

/// <summary>Thrown by the throwing parse and write entry points when data cannot be encoded or decoded.</summary>
public sealed class OscFormatException : FormatException
{
    public OscFormatException(OscParseError error) : base($"Invalid OSC data: {error}.")
    {
        Error = error;
    }

    public OscFormatException(string message) : base(message)
    {
    }

    public OscFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public OscFormatException()
    {
    }

    /// <summary>The parse error, or <see cref="OscParseError.None"/> when the failure was on the writing side.</summary>
    public OscParseError Error { get; }
}
