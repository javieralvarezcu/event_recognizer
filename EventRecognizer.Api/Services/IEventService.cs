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
}
