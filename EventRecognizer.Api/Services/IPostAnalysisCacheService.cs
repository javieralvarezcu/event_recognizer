using EventRecognizer.Api.Models;

namespace EventRecognizer.Api.Services;

/// <summary>
/// Lookup/storage of the LLM analysis of Instagram posts, keyed by content hash,
/// so repeated submissions of the same posts do not burn tokens re-analyzing them.
/// Also coordinates concurrent identical requests (in-flight dedup): only one of
/// them pays the LLM call, the rest await its result.
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
    /// Registers the hash as being analyzed right now. Returns true (and the task
    /// to complete later) when this caller became the owner and must run the LLM
    /// call; returns false (and the in-flight task to await) when another request
    /// is already analyzing the same post.
    /// </summary>
    bool TryRegisterInflight(string postHash, out Task<PostAnalysisResult> task);

    /// <summary>Completes the in-flight entry of one hash with its analysis.</summary>
    void CompleteInflight(string postHash, PostAnalysisResult result);

    /// <summary>
    /// Fails the in-flight entries (owner's analysis failed), so the awaiting
    /// requests retry the analysis themselves.
    /// </summary>
    void FailInflight(IReadOnlyList<string> postHashes);
}
