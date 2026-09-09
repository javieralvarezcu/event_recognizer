using EventRecognizer.Api.Models;

namespace EventRecognizer.Api.Services;

/// <summary>
/// Scrapes the muxojaleo.com calendar.
/// </summary>
public interface IMuxoScraperService
{
    /// <summary>
    /// Scrapes the calendar for the current month plus <paramref name="monthsAhead"/> following
    /// months and returns the events found, deduplicated by their external id.
    /// </summary>
    /// <param name="monthsAhead">How many months after the current one to also scrape.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ScrapeException">When the site cannot be fetched or parsed.</exception>
    Task<List<MuxoEvent>> ScrapeUpcomingAsync(int monthsAhead, CancellationToken ct = default);
}
