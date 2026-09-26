using System.Text.Json;
using EventRecognizer.Api.Data;
using EventRecognizer.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventRecognizer.Api.Services;

public class PostRegistryService : IPostRegistryService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<PostRegistryService> _logger;

    // Single analysis turn shared by all requests in the process: concurrent runs
    // serialize their analysis of new posts here instead of racing each other to
    // the LLM. Process-wide, so the service must be registered as a singleton.
    private readonly SemaphoreSlim _analyzeTurn = new(1, 1);

    public PostRegistryService(IDbContextFactory<AppDbContext> dbContextFactory, ILogger<PostRegistryService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<Dictionary<string, PostAnalysisResult>> GetAnalysesByUrlAsync(
        IReadOnlyList<string> urls,
        CancellationToken ct = default)
    {
        if (urls.Count == 0)
            return new Dictionary<string, PostAnalysisResult>();

        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            var rows = await db.Posts
                .AsNoTracking()
                .Where(p => urls.Contains(p.Url) && p.AnalysisJson != null)
                .ToListAsync(ct);

            var result = new Dictionary<string, PostAnalysisResult>(rows.Count);
            foreach (var row in rows)
            {
                var analysis = Deserialize(row.AnalysisJson);
                if (analysis != null)
                    result[row.Url] = analysis;
            }

            return result;
        }
        catch (Exception ex)
        {
            // Registry failures must never break recognition: treat as all-unknown.
            _logger.LogError(ex, "Failed to read the post registry");
            return new Dictionary<string, PostAnalysisResult>();
        }
    }

    public async Task RegisterPostsAsync(
        IReadOnlyList<InstagramPost> posts,
        CancellationToken ct = default)
    {
        if (posts.Count == 0)
            return;

        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            // Distinct by URL: repeated posts within the same batch share one row.
            var seen = new HashSet<string>();
            foreach (var post in posts)
            {
                if (!seen.Add(post.Url))
                    continue;

                db.Posts.Add(new PostRecord
                {
                    Url = post.Url,
                    Account = post.Account,
                    PostId = post.PostId,
                    Caption = post.Caption,
                    PostDatetime = post.Datetime,
                    ImageUrl = post.ImageUrl,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Registered {Count} new posts", seen.Count);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent instance registered the same URLs: ignore, the rows are there.
            _logger.LogWarning(ex, "Failed to register {Count} posts (URLs already registered?)", posts.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register {Count} posts", posts.Count);
        }
    }

    public async Task StoreAnalysesAsync(
        IReadOnlyList<(string Url, PostAnalysisResult Analysis)> entries,
        CancellationToken ct = default)
    {
        if (entries.Count == 0)
            return;

        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            var urls = entries.Select(e => e.Url).Distinct().ToList();
            var rows = await db.Posts.Where(p => urls.Contains(p.Url)).ToListAsync(ct);

            foreach (var (url, analysis) in entries)
            {
                var row = rows.FirstOrDefault(r => r.Url == url);
                if (row == null)
                    continue; // registered by another instance under a different connection state

                row.IsEvent = analysis.IsEvent;
                row.AnalysisJson = JsonSerializer.Serialize(analysis);
                row.AnalyzedAtUtc = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Stored the analysis of {Count} posts", rows.Count);
        }
        catch (Exception ex)
        {
            // Never break recognition for the registry: the posts stay with a null
            // analysis and will be re-analyzed by the next request.
            _logger.LogWarning(ex, "Failed to store {Count} post analyses", entries.Count);
        }
    }

    public Task WaitForAnalyzeTurnAsync(CancellationToken ct = default)
        => _analyzeTurn.WaitAsync(ct);

    public void ReleaseAnalyzeTurn()
        => _analyzeTurn.Release();

    private static PostAnalysisResult? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<PostAnalysisResult>(json);
        }
        catch (JsonException)
        {
            return null; // corrupt row: the post will be re-analyzed
        }
    }
}
