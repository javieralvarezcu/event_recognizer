using EventRecognizer.Api.Dtos;
using EventRecognizer.Api.Models;

namespace EventRecognizer.Api.Services;

/// <summary>
/// Service for event recognition and persistence.
/// </summary>
public interface IEventService
{
    /// <summary>
    /// Processes a batch of Instagram posts, recognizes events via LLM, and persists them.
    /// </summary>
    /// <param name="posts">The Instagram posts to analyze.</param>
    /// <param name="deepSeekApiKey">The DeepSeek API key from the request header.</param>
    /// <param name="dateRange">Optional date range of valid events. When provided, an event is
    /// only considered found if it (or its recurrence) occurs within the range; otherwise the
    /// post is returned as a non-event and nothing is persisted for it.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RecognitionResponse> RecognizeEventsAsync(
        List<InstagramPost> posts,
        string deepSeekApiKey,
        DateRange? dateRange = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves an event by its unique event identifier.
    /// </summary>
    Task<EventDetailResponse?> GetEventByUniqueIdAsync(string eventUniqueId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all persisted events ordered by effective date (event date or recurrence
    /// start), then creation time. Events without any computable date come last.
    /// </summary>
    Task<List<EventDetailResponse>> GetAllEventsAsync(CancellationToken ct = default);

    /// <summary>
    /// Cleans a month of duplicated events: sends the month's events (plus the events
    /// without any date) to the LLM, which groups duplicates, and deletes all events
    /// marked as duplicates except the one kept per group.
    /// </summary>
    /// <param name="year">Year of the month to clean.</param>
    /// <param name="month">Month to clean (1-12).</param>
    /// <param name="deepSeekApiKey">The DeepSeek API key from the request header.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CleanupResponse> CleanupMonthAsync(
        int year,
        int month,
        string deepSeekApiKey,
        CancellationToken ct = default);

    /// <summary>
    /// Scrapes the muxojaleo.com calendar (persisting new events), cross-matches our
    /// persisted events against it via the LLM, and persists the new matches.
    /// </summary>
    /// <param name="deepSeekApiKey">The DeepSeek API key from the request header.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CrossCheckResponse> CrossCheckAsync(string deepSeekApiKey, CancellationToken ct = default);
}
