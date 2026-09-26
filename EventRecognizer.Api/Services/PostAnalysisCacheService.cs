using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EventRecognizer.Api.Data;
using EventRecognizer.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventRecognizer.Api.Services;

public class PostAnalysisCacheService : IPostAnalysisCacheService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<PostAnalysisCacheService> _logger;

    // Hashes currently being analyzed by some request: concurrent identical runs
    // await the same analysis instead of each paying a DeepSeek call. Process-wide,
    // so the service must be registered as a singleton.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PostAnalysisResult>> _inflight = new();

    public PostAnalysisCacheService(IDbContextFactory<AppDbContext> dbContextFactory, ILogger<PostAnalysisCacheService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Content fingerprint of one post for caching: the post itself plus the
    /// requested date range (the range influences how relative dates are resolved,
    /// so a different range must not reuse a cached analysis). Dates are normalized
    /// to day granularity — the prompt only carries yyyy-MM-dd, so a range that
    /// drifts within the same day must hit the same cache entry.
    /// </summary>
    public static string ComputePostHash(InstagramPost post, DateRange? dateRange)
    {
        var sb = new StringBuilder();
        sb.Append(post.Account).Append('\n');
        sb.Append(post.Caption).Append('\n');
        sb.Append(post.Datetime?.ToString("O")).Append('\n');
        sb.Append(post.Url).Append('\n');
        sb.Append(dateRange?.From?.Date.ToString("yyyy-MM-dd")).Append('|').Append(dateRange?.To?.Date.ToString("yyyy-MM-dd"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public bool TryRegisterInflight(string postHash, out Task<PostAnalysisResult> task)
    {
        var tcs = new TaskCompletionSource<PostAnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_inflight.TryAdd(postHash, tcs))
        {
            task = tcs.Task;
            return true; // this caller owns the analysis of the hash
        }

        task = _inflight[postHash].Task;
        return false; // another request is already analyzing it
    }

    public void CompleteInflight(string postHash, PostAnalysisResult result)
    {
        if (_inflight.TryRemove(postHash, out var tcs))
            tcs.TrySetResult(result);
    }

    public void FailInflight(IReadOnlyList<string> postHashes)
    {
        foreach (var hash in postHashes)
        {
            if (_inflight.TryRemove(hash, out var tcs))
                tcs.TrySetException(new InvalidOperationException("The in-flight post analysis failed."));
        }
    }

    public async Task<Dictionary<string, PostAnalysisResult>> GetCachedAsync(
        IReadOnlyList<string> postHashes,
        CancellationToken ct = default)
    {
        if (postHashes.Count == 0)
            return new Dictionary<string, PostAnalysisResult>();

        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            var rows = await db.PostAnalysisCaches
                .AsNoTracking()
                .Where(c => postHashes.Contains(c.PostHash))
                .ToListAsync(ct);

            var result = new Dictionary<string, PostAnalysisResult>(rows.Count);
            foreach (var row in rows)
            {
                var analysis = Deserialize(row.AnalysisJson);
                if (analysis != null)
                    result[row.PostHash] = analysis;
            }

            return result;
        }
        catch (Exception ex)
        {
            // Cache failures must never break recognition: treat as all-miss.
            _logger.LogError(ex, "Failed to read the post analysis cache");
            return new Dictionary<string, PostAnalysisResult>();
        }
    }

    public async Task StoreAsync(
        IReadOnlyList<(string PostHash, string PostId, PostAnalysisResult Analysis)> entries,
        CancellationToken ct = default)
    {
        if (entries.Count == 0)
            return;

        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            foreach (var (postHash, postId, analysis) in entries)
            {
                db.PostAnalysisCaches.Add(new PostAnalysisCache
                {
                    PostHash = postHash,
                    PostId = postId,
                    AnalysisJson = JsonSerializer.Serialize(analysis),
                    CreatedAt = DateTime.UtcNow
                });
            }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Cached {Count} post analyses", entries.Count);
        }
        catch (Exception ex)
        {
            // A concurrent run may have inserted the same hash: the unique index
            // would make the save fail. Never break recognition for the cache.
            _logger.LogWarning(ex, "Failed to store {Count} post analyses in the cache", entries.Count);
        }
    }

    private static PostAnalysisResult? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PostAnalysisResult>(json);
        }
        catch (JsonException)
        {
            return null; // corrupt row: ignore it, the post will be re-analyzed
        }
    }
}
