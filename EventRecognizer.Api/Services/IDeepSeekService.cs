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
}
