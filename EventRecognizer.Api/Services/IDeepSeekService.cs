using EventRecognizer.Api.Models;

namespace EventRecognizer.Api.Services;

/// <summary>
/// Service for calling the DeepSeek LLM API to analyze Instagram posts.
/// </summary>
public interface IDeepSeekService
{
    /// <summary>
    /// Sends a batch of posts to DeepSeek and returns structured analysis results.
    /// </summary>
    /// <param name="posts">The Instagram posts to analyze.</param>
    /// <param name="apiKey">The DeepSeek API key provided by the client in the request header.</param>
    /// <param name="dateRange">Optional date range the client is interested in. Passed to the
    /// LLM as context for resolving relative/approximate dates; the server applies the final filter.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<PostAnalysisResult>> AnalyzePostsAsync(
        List<InstagramPost> posts,
        string apiKey,
        DateRange? dateRange = null,
        CancellationToken ct = default);

    /// <summary>
    /// Asks the LLM to group the given events into duplicate sets (same real-world event),
    /// returning for each group the id to keep and the ids to delete.
    /// </summary>
    /// <param name="events">The persisted events to compare. Only these ids may be referenced.</param>
    /// <param name="monthLabel">The month being cleaned, "yyyy-MM", for context.</param>
    /// <param name="apiKey">The DeepSeek API key provided by the client in the request header.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<DuplicateGroupResult>> FindDuplicateEventsAsync(
        List<CleanupEventItem> events,
        string monthLabel,
        string apiKey,
        CancellationToken ct = default);

    /// <summary>
    /// Asks the LLM to cross-match our persisted events with the events of the
    /// muxojaleo.com calendar, returning the pairs that describe the same real event.
    /// </summary>
    /// <param name="ourEvents">Our persisted events. Only these ids may be referenced.</param>
    /// <param name="muxoEvents">Events scraped from muxojaleo.com. Only these ids may be referenced.</param>
    /// <param name="apiKey">The DeepSeek API key provided by the client in the request header.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<CrossMatchResult>> FindCrossMatchesAsync(
        List<CleanupEventItem> ourEvents,
        List<MuxoEventItem> muxoEvents,
        string apiKey,
        CancellationToken ct = default);
}
