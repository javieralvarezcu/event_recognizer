using EventRecognizer.Api.Models;

namespace EventRecognizer.Api.Services;

/// <summary>
/// Lookup/storage of the LLM analysis of Instagram posts, keyed by content hash,
/// so repeated submissions of the same posts do not burn tokens re-analyzing them.
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
}
