using System.Security.Cryptography;
using System.Text;
using EventRecognizer.Api.Data;
using EventRecognizer.Api.Dtos;
using EventRecognizer.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventRecognizer.Api.Services;

public class EventService : IEventService
{
    // How many months after the current one are scraped on each crosscheck run.
    private const int MuxoMonthsAhead = 2;

    private readonly IDeepSeekService _deepSeekService;
    private readonly IMuxoScraperService _muxoScraperService;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<EventService> _logger;

    public EventService(
        IDeepSeekService deepSeekService,
        IMuxoScraperService muxoScraperService,
        AppDbContext dbContext,
        ILogger<EventService> logger)
    {
        _deepSeekService = deepSeekService;
        _muxoScraperService = muxoScraperService;
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

        var match = await _dbContext.CrossMatches
            .Include(m => m.MuxoEvent)
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.EventUniqueId == eventUniqueId, ct);

        return MapToDetailDto(record, match);
    }

    public async Task<List<EventDetailResponse>> GetAllEventsAsync(CancellationToken ct = default)
    {
        var records = await _dbContext.EventRecords
            .AsNoTracking()
            .OrderBy(e => (e.EventDate ?? e.RecurrenceStartDate) == null)
            .ThenBy(e => e.EventDate ?? e.RecurrenceStartDate)
            .ThenBy(e => e.CreatedAt)
            .ToListAsync(ct);

        var matches = await _dbContext.CrossMatches
            .Include(m => m.MuxoEvent)
            .AsNoTracking()
            .Where(m => records.Select(r => r.EventUniqueId).Contains(m.EventUniqueId))
            .ToDictionaryAsync(m => m.EventUniqueId, ct);

        return records.Select(r => MapToDetailDto(r, matches.GetValueOrDefault(r.EventUniqueId))).ToList();
    }

    public async Task<CrossCheckResponse> CrossCheckAsync(string deepSeekApiKey, CancellationToken ct = default)
    {
        // 1. Scrape the muxojaleo calendar and upsert the events (dedupe by external id,
        //    updating the fields when the site changed them).
        var scraped = await _muxoScraperService.ScrapeUpcomingAsync(MuxoMonthsAhead, ct);

        var externalIds = scraped.Select(e => e.ExternalId).ToList();
        var storedByExternalId = await _dbContext.MuxoEvents
            .Where(m => externalIds.Contains(m.ExternalId))
            .ToDictionaryAsync(m => m.ExternalId, ct);

        var newMuxoEvents = new List<MuxoEvent>();
        var changed = false;
        foreach (var scrapedEvent in scraped)
        {
            if (storedByExternalId.TryGetValue(scrapedEvent.ExternalId, out var stored))
            {
                if (stored.Title != scrapedEvent.Title || stored.Date != scrapedEvent.Date ||
                    stored.Venue != scrapedEvent.Venue || stored.Link != scrapedEvent.Link ||
                    stored.Categories != scrapedEvent.Categories || stored.Price != scrapedEvent.Price)
                {
                    stored.Title = scrapedEvent.Title;
                    stored.Date = scrapedEvent.Date;
                    stored.Venue = scrapedEvent.Venue;
                    stored.Link = scrapedEvent.Link;
                    stored.Categories = scrapedEvent.Categories;
                    stored.Price = scrapedEvent.Price;
                    changed = true;
                }
            }
            else
            {
                newMuxoEvents.Add(scrapedEvent);
                changed = true;
            }
        }

        if (newMuxoEvents.Count > 0)
            _dbContext.MuxoEvents.AddRange(newMuxoEvents);

        if (changed)
            await _dbContext.SaveChangesAsync(ct);

        // 2. Ask the LLM for matches between our events and the muxojaleo events.
        var ourEvents = await _dbContext.EventRecords.AsNoTracking().ToListAsync(ct);
        var muxoEvents = await _dbContext.MuxoEvents.AsNoTracking().ToListAsync(ct);

        List<CrossMatchResult> matches = new();
        if (ourEvents.Count > 0 && muxoEvents.Count > 0)
        {
            var ourItems = ourEvents.Select(ToCleanupItem).ToList();
            var muxoItems = muxoEvents.Select(m => new MuxoEventItem
            {
                ExternalId = m.ExternalId,
                Title = m.Title,
                Date = m.Date,
                Venue = m.Venue,
                Categories = m.Categories,
                Link = m.Link
            }).ToList();

            matches = await _deepSeekService.FindCrossMatchesAsync(ourItems, muxoItems, deepSeekApiKey, ct);
        }

        // 3. Persist only new, valid matches (both ids must exist and the pair must not
        //    be persisted yet; each event and each muxo event matches at most once).
        var validOurIds = ourEvents.Select(e => e.EventUniqueId).ToHashSet();
        var muxoIdByExternalId = muxoEvents.ToDictionary(m => m.ExternalId);
        var existingPairs = await _dbContext.CrossMatches
            .AsNoTracking()
            .Select(m => new { m.EventUniqueId, m.MuxoEventId })
            .ToListAsync(ct);

        var usedMuxoIds = existingPairs.Select(p => p.MuxoEventId).ToHashSet();
        var usedEventIds = existingPairs.Select(p => p.EventUniqueId).ToHashSet();

        var newMatches = new List<CrossMatch>();
        foreach (var pair in matches)
        {
            if (!validOurIds.Contains(pair.EventUniqueId) || usedEventIds.Contains(pair.EventUniqueId))
                continue;
            if (!muxoIdByExternalId.TryGetValue(pair.MuxoEventId, out var muxoEvent) ||
                usedMuxoIds.Contains(muxoEvent.Id))
                continue;

            newMatches.Add(new CrossMatch
            {
                EventUniqueId = pair.EventUniqueId,
                MuxoEventId = muxoEvent.Id,
                Reason = pair.Reason
            });
            usedEventIds.Add(pair.EventUniqueId);
            usedMuxoIds.Add(muxoEvent.Id);
        }

        if (newMatches.Count > 0)
        {
            _dbContext.CrossMatches.AddRange(newMatches);
            await _dbContext.SaveChangesAsync(ct);
            _logger.LogInformation("Crosscheck persisted {Count} new matches", newMatches.Count);
        }

        // 4. Build the response with the details of the new matches.
        var ourById = ourEvents.ToDictionary(e => e.EventUniqueId);
        var response = new CrossCheckResponse
        {
            MuxoEventsScraped = scraped.Count,
            MuxoEventsNew = newMuxoEvents.Count,
            OurEventsAnalyzed = ourEvents.Count,
            MatchesFound = newMatches.Count
        };

        foreach (var match in newMatches)
        {
            var muxoEvent = muxoIdByExternalId.Values.SingleOrDefault(m => m.Id == match.MuxoEventId);
            response.Matches.Add(new CrossMatchDto
            {
                EventUniqueId = match.EventUniqueId,
                EventTitle = ourById.GetValueOrDefault(match.EventUniqueId)?.Title,
                MuxoTitle = muxoEvent?.Title,
                MuxoDate = muxoEvent?.Date,
                MuxoLink = muxoEvent?.Link,
                Reason = match.Reason
            });
        }

        return response;
    }

    public async Task<CleanupResponse> CleanupMonthAsync(
        int year,
        int month,
        string deepSeekApiKey,
        CancellationToken ct = default)
    {
        var monthLabel = $"{year:0000}-{month:00}";
        var monthStart = new DateTime(year, month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        var records = await _dbContext.EventRecords.AsNoTracking().ToListAsync(ct);

        // Analyze: events occurring within the month + events without any computable
        // date (those in the "sin fecha" list, crossed against the month's events).
        var analyzed = records
            .Where(r => OccursInMonth(r, monthStart, monthEnd) || HasNoDate(r))
            .ToList();

        if (analyzed.Count == 0)
            return new CleanupResponse { Month = monthLabel };

        var items = analyzed.Select(ToCleanupItem).ToList();
        var groups = await _deepSeekService.FindDuplicateEventsAsync(items, monthLabel, deepSeekApiKey, ct);

        // Only ids that were sent to the LLM can be deleted, and an id chosen as the
        // keeper of any group is protected. Groups whose keeper is unknown are skipped
        // entirely so a bogus keeper can't cause a whole group to be deleted.
        var analyzedIds = analyzed.Select(r => r.EventUniqueId).ToHashSet();
        var recordsById = analyzed.ToDictionary(r => r.EventUniqueId);
        var keepIds = groups
            .Where(g => analyzedIds.Contains(g.KeepEventId))
            .Select(g => g.KeepEventId)
            .ToHashSet();
        var toDelete = groups
            .Where(g => analyzedIds.Contains(g.KeepEventId))
            .SelectMany(g => g.DuplicateEventIds)
            .Where(analyzedIds.Contains)
            .Where(id => !keepIds.Contains(id))
            .Distinct()
            .ToHashSet();

        if (toDelete.Count > 0)
        {
            var entities = await _dbContext.EventRecords
                .Where(e => toDelete.Contains(e.EventUniqueId))
                .ToListAsync(ct);
            _dbContext.EventRecords.RemoveRange(entities);
            await _dbContext.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Cleanup removed {Count} duplicate events for month {Month}", entities.Count, monthLabel);
        }

        var response = new CleanupResponse { Month = monthLabel, EventsAnalyzed = analyzed.Count };
        foreach (var group in groups)
        {
            if (!analyzedIds.Contains(group.KeepEventId))
                continue;

            var removed = group.DuplicateEventIds
                .Where(toDelete.Contains)
                .Select(id => recordsById[id])
                .ToList();
            if (removed.Count == 0)
                continue;

            response.Groups.Add(new CleanupGroupDto
            {
                KeepEventId = group.KeepEventId,
                KeepTitle = recordsById[group.KeepEventId].Title,
                Removed = removed
                    .Select(r => new CleanupRemovedDto
                    {
                        EventUniqueId = r.EventUniqueId,
                        Title = r.Title,
                        Account = r.Account
                    })
                    .ToList(),
                Reason = group.Reason
            });
        }

        response.DeletedCount = response.Groups.Sum(g => g.Removed.Count);
        return response;
    }

    /// <summary>
    /// Whether the event (or one of its recurrences) occurs on any day of [from, to].
    /// Reuses the same day-granularity logic as recognition filtering.
    /// </summary>
    private static bool OccursInMonth(EventRecord r, DateTime from, DateTime to)
    {
        var analysis = new PostAnalysisResult
        {
            EventDate = r.EventDate?.ToString("yyyy-MM-dd"),
            RecurrenceStartDate = r.RecurrenceStartDate?.ToString("yyyy-MM-dd"),
            RecurrenceEndDate = r.RecurrenceEndDate?.ToString("yyyy-MM-dd"),
            RecurrenceDaysOfWeek = ParseRecurrenceDays(r.RecurrenceDaysOfWeek)
        };
        return RecurrenceEvaluator.OccursInRange(analysis, from, to);
    }

    private static bool HasNoDate(EventRecord r)
        => r.EventDate == null && r.RecurrenceStartDate == null && r.RecurrenceEndDate == null;

    private static CleanupEventItem ToCleanupItem(EventRecord r)
        => new()
        {
            EventUniqueId = r.EventUniqueId,
            Title = r.Title,
            Summary = r.Summary,
            EventDate = r.EventDate,
            EventDateDescription = r.EventDateDescription,
            IsRecurrent = r.IsRecurrent,
            RecurrenceType = r.RecurrenceType,
            RecurrenceDaysOfWeek = r.RecurrenceDaysOfWeek,
            RecurrenceStartDate = r.RecurrenceStartDate,
            RecurrenceEndDate = r.RecurrenceEndDate,
            Account = r.Account,
            Caption = r.Caption,
            Url = r.Url
        };

    private static List<int>? ParseRecurrenceDays(string? days)
    {
        if (string.IsNullOrWhiteSpace(days))
            return null;

        return days.Split(',')
            .Select(s => int.TryParse(s.Trim(), out var n) ? n : (int?)null)
            .Where(n => n.HasValue)
            .Select(n => n!.Value)
            .ToList();
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

    private static EventDetailResponse MapToDetailDto(EventRecord e, CrossMatch? match = null)
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
            CreatedAt = e.CreatedAt,
            IsCrossed = match != null,
            MuxoTitle = match?.MuxoEvent?.Title,
            MuxoLink = match?.MuxoEvent?.Link,
            MuxoDate = match?.MuxoEvent?.Date
        };
    }
}
