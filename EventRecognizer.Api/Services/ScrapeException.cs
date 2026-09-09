namespace EventRecognizer.Api.Services;

/// <summary>
/// Thrown when the muxojaleo.com calendar cannot be fetched or parsed.
/// </summary>
public class ScrapeException : Exception
{
    public ScrapeException(string message) : base(message)
    {
    }

    public ScrapeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
