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
        DateRange? dateRange = null,
        CancellationToken ct = default)
    {
        // 1. Send all posts to DeepSeek for analysis
        var analyses = await _deepSeekService.AnalyzePostsAsync(posts, deepSeekApiKey, dateRange, ct);

        // 2. Decide which posts are valid events for the requested date range. A post is
        //    valid when the LLM says it is an event AND, if a range was requested, the
        //    event (or its recurrence) occurs within that range.
        var isValidEvent = new bool[analyses.Count];
        for (var i = 0; i < analyses.Count; i++)
        {
            isValidEvent[i] = analyses[i].IsEvent
                && (dateRange == null
                    || RecurrenceEvaluator.OccursInRange(analyses[i], dateRange.From, dateRange.To));
        }

        // 3. Build event records for valid event posts.
        //    Keyed by PostId so repeated posts within the same batch are only processed once.
        var eventPosts = new Dictionary<string, EventRecord>();
        for (int i = 0; i < posts.Count && i < analyses.Count; i++)
        {
            if (!isValidEvent[i])
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
                IsRecurrent = analysis.IsRecurrent,
                RecurrenceType = string.IsNullOrWhiteSpace(analysis.RecurrenceType)
                    ? null
                    : Truncate(analysis.RecurrenceType, 20),
                RecurrenceDaysOfWeek = FormatRecurrenceDays(analysis.RecurrenceDaysOfWeek),
                RecurrenceStartDate = ParseEventDate(analysis.RecurrenceStartDate),
                RecurrenceEndDate = ParseEventDate(analysis.RecurrenceEndDate),
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

        // 4. Check for duplicates (skip existing post_ids) and persist only new events
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

        // 5. Fetch existing events from DB so we return full data for duplicates too
        var allEventPostIds = eventRecords.Select(e => e.PostId).ToHashSet();
        var existingEvents = await _dbContext.EventRecords
            .Where(e => allEventPostIds.Contains(e.PostId))
            .ToListAsync(ct);

        // 6. Build response — return ALL posts (event + non-event)
        return MapToResponse(posts, analyses, existingEvents, isValidEvent);
    }

    public async Task<EventDetailResponse?> GetEventByUniqueIdAsync(string eventUniqueId, CancellationToken ct = default)
    {
        var record = await _dbContext.EventRecords
            .FirstOrDefaultAsync(e => e.EventUniqueId == eventUniqueId, ct);

        if (record == null)
            return null;

        return MapToDetailDto(record);
    }

    public async Task<List<EventDetailResponse>> GetAllEventsAsync(CancellationToken ct = default)
    {
        var records = await _dbContext.EventRecords
            .AsNoTracking()
            .OrderBy(e => (e.EventDate ?? e.RecurrenceStartDate) == null)
            .ThenBy(e => e.EventDate ?? e.RecurrenceStartDate)
            .ThenBy(e => e.CreatedAt)
            .ToListAsync(ct);

        return records.Select(MapToDetailDto).ToList();
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
        List<EventRecord> dbEvents,
        bool[] isValidEvent)
    {
        var dbEventsByPostId = dbEvents.ToDictionary(e => e.PostId);

        var results = new List<RecognizedEventDto>(posts.Count);
        var seenPostIds = new HashSet<string>();
        for (int i = 0; i < posts.Count && i < analyses.Count; i++)
        {
            var post = posts[i];
            if (!seenPostIds.Add(post.PostId))
                continue;

            if (isValidEvent[i] && dbEventsByPostId.TryGetValue(post.PostId, out var dbEvent))
            {
                // Valid event post — return full event data from DB
                results.Add(MapToDto(dbEvent, isEvent: true));
            }
            else
            {
                // Non-event (or out-of-range) post — return only post fields, no event data
                results.Add(new RecognizedEventDto
                {
                    IsEvent = false,
                    Account = post.Account,
                    PostId = post.PostId,
                    Caption = post.Caption ?? string.Empty,
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
            IsRecurrent = e.IsRecurrent,
            RecurrenceType = e.RecurrenceType,
            RecurrenceDaysOfWeek = e.RecurrenceDaysOfWeek,
            RecurrenceStartDate = e.RecurrenceStartDate,
            RecurrenceEndDate = e.RecurrenceEndDate,
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

    /// <summary>
    /// Stores the LLM weekday list as a comma-separated string ("1,2,3,4") for the DB column.
    /// </summary>
    private static string? FormatRecurrenceDays(List<int>? days)
        => days is { Count: > 0 } ? string.Join(",", days) : null;

    private static EventDetailResponse MapToDetailDto(EventRecord e)
    {
        return new EventDetailResponse
        {
            EventUniqueId = e.EventUniqueId,
            Title = e.Title,
            EventDate = e.EventDate,
            EventDateDescription = e.EventDateDescription,
            Summary = e.Summary,
            IsRecurrent = e.IsRecurrent,
            RecurrenceType = e.RecurrenceType,
            RecurrenceDaysOfWeek = e.RecurrenceDaysOfWeek,
            RecurrenceStartDate = e.RecurrenceStartDate,
            RecurrenceEndDate = e.RecurrenceEndDate,
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
