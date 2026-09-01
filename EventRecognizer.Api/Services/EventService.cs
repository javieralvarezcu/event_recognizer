using System.Security.Cryptography;
using System.Text;
using EventRecognizer.Api.Data;
using EventRecognizer.Api.Dtos;
using EventRecognizer.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventRecognizer.Api.Services;

public class EventService : IEventService
{
    private readonly IDeepSeekService _deepSeekService;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<EventService> _logger;

    public EventService(IDeepSeekService deepSeekService, AppDbContext dbContext, ILogger<EventService> logger)
    {
        _deepSeekService = deepSeekService;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<RecognitionResponse> RecognizeEventsAsync(
        List<InstagramPost> posts,
        string deepSeekApiKey,
        CancellationToken ct = default)
    {
        // 1. Send all posts to DeepSeek for analysis
        var analyses = await _deepSeekService.AnalyzePostsAsync(posts, deepSeekApiKey, ct);

        // 2. Build event records for posts identified as events.
        //    Keyed by PostId so repeated posts within the same batch are only processed once.
        var eventPosts = new Dictionary<string, EventRecord>();
        for (int i = 0; i < posts.Count && i < analyses.Count; i++)
        {
            if (!analyses[i].IsEvent)
                continue;

            var post = posts[i];
            if (eventPosts.ContainsKey(post.PostId))
                continue;

            var analysis = analyses[i];

            var eventRecord = new EventRecord
            {
                EventUniqueId = GenerateEventUniqueId(post, analysis),
                Title = analysis.Title ?? "Sin título",
                EventDate = ParseEventDate(analysis.EventDate),
                EventDateDescription = analysis.EventDateDescription,
                Summary = analysis.Summary ?? "Sin resumen",
                Account = post.Account,
                PostId = post.PostId,
                Caption = Truncate(post.Caption, 4000),
                PostDatetime = post.Datetime,
                Url = post.Url,
                ImageUrl = post.ImageUrl,
                CreatedAt = DateTime.UtcNow
            };

            eventPosts[post.PostId] = eventRecord;
        }

        // 3. Check for duplicates (skip existing post_ids) and persist only new events
        var eventRecords = eventPosts.Values.ToList();

        var existingPostIds = await _dbContext.EventRecords
            .Where(e => eventRecords.Select(ep => ep.PostId).Contains(e.PostId))
            .Select(e => e.PostId)
            .ToListAsync(ct);

        var newEvents = eventRecords
            .Where(ep => !existingPostIds.Contains(ep.PostId))
            .ToList();

        if (newEvents.Count > 0)
        {
            _dbContext.EventRecords.AddRange(newEvents);

            try
            {
                await _dbContext.SaveChangesAsync(ct);
                _logger.LogInformation("Persisted {Count} new events", newEvents.Count);
            }
            catch (DbUpdateException ex)
            {
                // Race: a concurrent request inserted one or more of these events after
                // our duplicate check. Detach the pending inserts, re-check against the
                // DB and retry only the ones that are still missing.
                _logger.LogWarning(ex, "Duplicate key conflict while saving events. Re-checking and retrying.");

                foreach (var entry in _dbContext.ChangeTracker.Entries<EventRecord>().ToList())
                {
                    _dbContext.Entry(entry.Entity).State = EntityState.Detached;
                }

                var nowExistingPostIds = await _dbContext.EventRecords
                    .Where(e => newEvents.Select(n => n.PostId).Contains(e.PostId))
                    .Select(e => e.PostId)
                    .ToListAsync(ct);

                var remaining = newEvents
                    .Where(n => !nowExistingPostIds.Contains(n.PostId))
                    .ToList();

                if (remaining.Count > 0)
                {
                    _dbContext.EventRecords.AddRange(remaining);
                    await _dbContext.SaveChangesAsync(ct);
                    _logger.LogInformation("Persisted {Count} new events after retry", remaining.Count);
                }
            }
        }

        // 4. Fetch existing events from DB so we return full data for duplicates too
        var allEventPostIds = eventRecords.Select(e => e.PostId).ToHashSet();
        var existingEvents = await _dbContext.EventRecords
            .Where(e => allEventPostIds.Contains(e.PostId))
            .ToListAsync(ct);

        // 5. Build response — return ALL posts (event + non-event)
        return MapToResponse(posts, analyses, existingEvents);
    }

    public async Task<EventDetailResponse?> GetEventByUniqueIdAsync(string eventUniqueId, CancellationToken ct = default)
    {
        var record = await _dbContext.EventRecords
            .FirstOrDefaultAsync(e => e.EventUniqueId == eventUniqueId, ct);

        if (record == null)
            return null;

        return MapToDetailDto(record);
    }

    private static string GenerateEventUniqueId(InstagramPost post, PostAnalysisResult analysis)
    {
        var raw = $"{post.PostId}-{post.Account}-{analysis.Title}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..12];
        var datePart = DateTime.UtcNow.ToString("yyyyMMdd");
        return $"EVT-{datePart}-{hash}";
    }

    private static DateTime? ParseEventDate(string? eventDateStr)
    {
        if (string.IsNullOrWhiteSpace(eventDateStr))
            return null;

        if (DateTime.TryParse(eventDateStr, out var parsed))
            return parsed.ToUniversalTime();

        return null;
    }

    private static RecognitionResponse MapToResponse(
        List<InstagramPost> posts,
        List<PostAnalysisResult> analyses,
        List<EventRecord> dbEvents)
    {
        var dbEventsByPostId = dbEvents.ToDictionary(e => e.PostId);

        var results = new List<RecognizedEventDto>(posts.Count);
        var seenPostIds = new HashSet<string>();
        for (int i = 0; i < posts.Count && i < analyses.Count; i++)
        {
            var post = posts[i];
            if (!seenPostIds.Add(post.PostId))
                continue;

            var analysis = analyses[i];

            if (analysis.IsEvent && dbEventsByPostId.TryGetValue(post.PostId, out var dbEvent))
            {
                // Event post — return full event data from DB
                results.Add(MapToDto(dbEvent, isEvent: true));
            }
            else
            {
                // Non-event post — return only post fields, no event data
                results.Add(new RecognizedEventDto
                {
                    IsEvent = false,
                    Account = post.Account,
                    PostId = post.PostId,
                    Caption = post.Caption,
                    PostDatetime = post.Datetime,
                    Url = post.Url,
                    ImageUrl = post.ImageUrl,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        return new RecognitionResponse
        {
            TotalPosts = posts.Count,
            EventsFound = results.Count(r => r.IsEvent),
            Events = results
        };
    }

    private static RecognizedEventDto MapToDto(EventRecord e, bool isEvent = true)
    {
        return new RecognizedEventDto
        {
            IsEvent = isEvent,
            EventUniqueId = e.EventUniqueId,
            Title = e.Title,
            EventDate = e.EventDate,
            EventDateDescription = e.EventDateDescription,
            Summary = e.Summary,
            Account = e.Account,
            PostId = e.PostId,
            Caption = e.Caption,
            PostDatetime = e.PostDatetime,
            Url = e.Url,
            ImageUrl = e.ImageUrl,
            CreatedAt = e.CreatedAt
        };
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static EventDetailResponse MapToDetailDto(EventRecord e)
    {
        return new EventDetailResponse
        {
            EventUniqueId = e.EventUniqueId,
            Title = e.Title,
            EventDate = e.EventDate,
            EventDateDescription = e.EventDateDescription,
            Summary = e.Summary,
            Account = e.Account,
            PostId = e.PostId,
            Caption = e.Caption,
            PostDatetime = e.PostDatetime,
            Url = e.Url,
            ImageUrl = e.ImageUrl,
            CreatedAt = e.CreatedAt
        };
    }
}
