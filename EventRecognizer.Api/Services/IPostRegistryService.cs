using EventRecognizer.Api.Models;

namespace EventRecognizer.Api.Services;

/// <summary>
/// Registry of every post received by the recognition endpoints, keyed by URL.
/// Posts already analyzed — events and non-events alike — skip the LLM entirely;
/// the LLM only sees URLs the system has not analyzed yet. Concurrent requests
/// serialize the analysis phase with a single turn so only the first one pays.
/// </summary>
public interface IPostRegistryService
{
    /// <summary>
    /// Returns the stored analyses of the given post URLs (only the already-analyzed ones).
    /// </summary>
    Task<Dictionary<string, PostAnalysisResult>> GetAnalysesByUrlAsync(
        IReadOnlyList<string> urls,
        CancellationToken ct = default);

    /// <summary>
    /// Registers the incoming posts (insert-or-ignore by URL) with a null analysis.
    /// </summary>
    Task RegisterPostsAsync(
        IReadOnlyList<InstagramPost> posts,
        CancellationToken ct = default);

    /// <summary>Stores the analyses of the given post URLs.</summary>
    Task StoreAnalysesAsync(
        IReadOnlyList<(string Url, PostAnalysisResult Analysis)> entries,
        CancellationToken ct = default);

    /// <summary>
    /// Acquires the single analysis turn. Concurrent requests queue here; the run
    /// holding the turn analyzes and stores its new posts, and each queued run
    /// re-checks the registry after acquiring — so concurrent identical runs pay
    /// one LLM pass between them (and cannot deadlock each other).
    /// </summary>
    Task WaitForAnalyzeTurnAsync(CancellationToken ct = default);

    /// <summary>Releases the analysis turn (always in a finally).</summary>
    void ReleaseAnalyzeTurn();
}
