using EventRecognizer.Api.Models;

namespace EventRecognizer.Api.Services;

/// <summary>
/// Lookup/storage of the LLM analysis of Instagram posts, keyed by content hash,
/// so repeated submissions of the same posts do not burn tokens re-analyzing them.
/// Concurrent requests serialize the analysis phase with a single turn: only the
/// first one pays the LLM call and the rest find everything in the cache.
/// </summary>
public interface IPostAnalysisCacheService
{
    /// <summary>
    /// Returns the cached analyses for the given post hashes (only the hits).
    /// </summary>
    Task<Dictionary<string, PostAnalysisResult>> GetCachedAsync(
        IReadOnlyList<string> postHashes,
        CancellationToken ct = default);

    /// <summary>Stores the freshly analyzed posts (hash → analysis).</summary>
    Task StoreAsync(
        IReadOnlyList<(string PostHash, string PostId, PostAnalysisResult Analysis)> entries,
        CancellationToken ct = default);

    /// <summary>
    /// Acquires the single analysis turn. Concurrent requests queue here; the run
    /// holding the turn analyzes its misses and stores them, and each queued run
    /// re-checks the cache after acquiring, so identical concurrent runs pay one
    /// LLM pass between them (and cannot deadlock each other).
    /// </summary>
    Task WaitForAnalyzeTurnAsync(CancellationToken ct = default);

    /// <summary>Releases the analysis turn (always in a finally).</summary>
    void ReleaseAnalyzeTurn();
}
