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
    /// <param name="ct">Cancellation token.</param>
    Task<RecognitionResponse> RecognizeEventsAsync(
        List<InstagramPost> posts,
        string deepSeekApiKey,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves an event by its unique event identifier.
    /// </summary>
    Task<EventDetailResponse?> GetEventByUniqueIdAsync(string eventUniqueId, CancellationToken ct = default);
}
