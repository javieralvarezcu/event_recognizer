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

    public PostAnalysisCacheService(IDbContextFactory<AppDbContext> dbContextFactory, ILogger<PostAnalysisCacheService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Content fingerprint of one post for caching: the post itself plus the
    /// requested date range (the range influences how relative dates are resolved,
    /// so a different range must not reuse a cached analysis).
    /// </summary>
    public static string ComputePostHash(InstagramPost post, DateRange? dateRange)
    {
        var sb = new StringBuilder();
        sb.Append(post.Account).Append('\n');
        sb.Append(post.Caption).Append('\n');
        sb.Append(post.Datetime?.ToString("O")).Append('\n');
        sb.Append(post.Url).Append('\n');
        sb.Append(dateRange?.From?.ToString("O")).Append('|').Append(dateRange?.To?.ToString("O"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
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
