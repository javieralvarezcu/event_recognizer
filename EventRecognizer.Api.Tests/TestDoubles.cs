using System.Net;
using System.Text;
using System.Text.Json;
using EventRecognizer.Api.Dtos;
using EventRecognizer.Api.Models;
using EventRecognizer.Api.Services;
using Microsoft.Extensions.Logging;

namespace EventRecognizer.Api.Tests;

/// <summary>
/// Stub HttpMessageHandler that serves pre-queued responses and records every request.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, string?, Task<HttpResponseMessage>>> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>Bodies captured per request, same order as <see cref="Requests"/>.</summary>
    public List<string?> RequestBodies { get; } = new();

    public void Enqueue(Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> responseFactory)
        => _responses.Enqueue(responseFactory);

    public void Enqueue(HttpStatusCode statusCode, string body)
        => _responses.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        // Capture the body while it is still readable — HttpClient disposes request
        // content once the handler returns.
        var body = request.Content != null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : null;
        RequestBodies.Add(body);
        return await _responses.Dequeue()(request, body);
    }
}

/// <summary>
/// Minimal IHttpClientFactory that always hands out a client backed by the same handler.
/// </summary>
public sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler);
}

/// <summary>
/// DeepSeekService with the retry backoff replaced by a no-op so tests run instantly.
/// </summary>
public sealed class TestableDeepSeekService : DeepSeekService
{
    public TestableDeepSeekService(IHttpClientFactory httpClientFactory, ILogger<DeepSeekService> logger)
        : base(httpClientFactory, logger)
    {
    }

    protected override Task DelayBetweenRetriesAsync(int attempt, CancellationToken ct)
        => Task.CompletedTask;
}

/// <summary>
/// IDeepSeekService fake driven by a delegate, so each test defines its own analysis results.
/// </summary>
public sealed class FakeDeepSeekService : IDeepSeekService
{
    private readonly Func<List<InstagramPost>, string, List<PostAnalysisResult>> _analyze;
    private readonly Func<List<CleanupEventItem>, string, List<DuplicateGroupResult>>? _findDuplicates;
    private readonly Func<List<CleanupEventItem>, List<MuxoEventItem>, List<CrossMatchResult>>? _findCrossMatches;

    public FakeDeepSeekService(
        Func<List<InstagramPost>, string, List<PostAnalysisResult>> analyze,
        Func<List<CleanupEventItem>, string, List<DuplicateGroupResult>>? findDuplicates = null,
        Func<List<CleanupEventItem>, List<MuxoEventItem>, List<CrossMatchResult>>? findCrossMatches = null)
    {
        _analyze = analyze;
        _findDuplicates = findDuplicates;
        _findCrossMatches = findCrossMatches;
    }

    public List<string> ReceivedApiKeys { get; } = new();

    public DateRange? LastDateRange { get; private set; }

    public List<CleanupEventItem>? LastCleanupEvents { get; private set; }

    public string? LastCleanupMonthLabel { get; private set; }

    public List<MuxoEventItem>? LastCrossMatchMuxoEvents { get; private set; }

    public Task<List<PostAnalysisResult>> AnalyzePostsAsync(
        List<InstagramPost> posts,
        string apiKey,
        DateRange? dateRange = null,
        CancellationToken ct = default)
    {
        ReceivedApiKeys.Add(apiKey);
        LastDateRange = dateRange;
        return Task.FromResult(_analyze(posts, apiKey));
    }

    public Task<List<DuplicateGroupResult>> FindDuplicateEventsAsync(
        List<CleanupEventItem> events,
        string monthLabel,
        string apiKey,
        CancellationToken ct = default)
    {
        LastCleanupEvents = events;
        LastCleanupMonthLabel = monthLabel;
        ReceivedApiKeys.Add(apiKey);
        return Task.FromResult(_findDuplicates != null
            ? _findDuplicates(events, monthLabel)
            : throw new InvalidOperationException("No findDuplicates delegate configured."));
    }

    public Task<List<CrossMatchResult>> FindCrossMatchesAsync(
        List<CleanupEventItem> ourEvents,
        List<MuxoEventItem> muxoEvents,
        string apiKey,
        CancellationToken ct = default)
    {
        LastCrossMatchMuxoEvents = muxoEvents;
        ReceivedApiKeys.Add(apiKey);
        return Task.FromResult(_findCrossMatches != null
            ? _findCrossMatches(ourEvents, muxoEvents)
            : throw new InvalidOperationException("No findCrossMatches delegate configured."));
    }
}

/// <summary>
/// IMuxoScraperService fake driven by a delegate.
/// </summary>
public sealed class FakeMuxoScraperService : IMuxoScraperService
{
    private readonly Func<int, List<MuxoEvent>> _scrape;

    public FakeMuxoScraperService(Func<int, List<MuxoEvent>> scrape)
        => _scrape = scrape;

    public int LastMonthsAhead { get; private set; } = -1;

    public Task<List<MuxoEvent>> ScrapeUpcomingAsync(int monthsAhead, CancellationToken ct = default)
    {
        LastMonthsAhead = monthsAhead;
        return Task.FromResult(_scrape(monthsAhead));
    }
}

/// <summary>
/// IEventService fake driven by delegates for the three endpoints.
/// </summary>
public sealed class FakeEventService : IEventService
{
    private readonly Func<List<InstagramPost>, string, CancellationToken, Task<RecognitionResponse>>? _recognize;
    private readonly Func<string, CancellationToken, Task<EventDetailResponse?>>? _getByUniqueId;
    private readonly Func<CancellationToken, Task<List<EventDetailResponse>>>? _getAll;
    private readonly Func<int, int, string, CancellationToken, Task<CleanupResponse>>? _cleanup;
    private readonly Func<string, CancellationToken, Task<CrossCheckResponse>>? _crossCheck;
    private readonly Func<CancellationToken, Task<List<MuxoEventDto>>>? _getAllMuxo;
    private readonly Func<string, UpdateEventRequest, CancellationToken, Task<EventDetailResponse?>>? _updateEvent;
    private readonly Func<string, CancellationToken, Task<bool>>? _deleteEvent;

    public FakeEventService(
        Func<List<InstagramPost>, string, CancellationToken, Task<RecognitionResponse>>? recognize = null,
        Func<string, CancellationToken, Task<EventDetailResponse?>>? getByUniqueId = null,
        Func<CancellationToken, Task<List<EventDetailResponse>>>? getAll = null,
        Func<int, int, string, CancellationToken, Task<CleanupResponse>>? cleanup = null,
        Func<string, CancellationToken, Task<CrossCheckResponse>>? crossCheck = null,
        Func<CancellationToken, Task<List<MuxoEventDto>>>? getAllMuxo = null,
        Func<string, UpdateEventRequest, CancellationToken, Task<EventDetailResponse?>>? updateEvent = null,
        Func<string, CancellationToken, Task<bool>>? deleteEvent = null)
    {
        _recognize = recognize;
        _getByUniqueId = getByUniqueId;
        _getAll = getAll;
        _cleanup = cleanup;
        _crossCheck = crossCheck;
        _getAllMuxo = getAllMuxo;
        _updateEvent = updateEvent;
        _deleteEvent = deleteEvent;
    }

    public DateRange? LastDateRange { get; private set; }

    public Task<RecognitionResponse> RecognizeEventsAsync(
        List<InstagramPost> posts,
        string deepSeekApiKey,
        DateRange? dateRange = null,
        CancellationToken ct = default)
    {
        LastDateRange = dateRange;
        return _recognize != null
            ? _recognize(posts, deepSeekApiKey, ct)
            : throw new InvalidOperationException("No recognize delegate configured.");
    }

    public Task<EventDetailResponse?> GetEventByUniqueIdAsync(string eventUniqueId, CancellationToken ct = default)
        => _getByUniqueId != null
            ? _getByUniqueId(eventUniqueId, ct)
            : throw new InvalidOperationException("No getByUniqueId delegate configured.");

    public Task<List<EventDetailResponse>> GetAllEventsAsync(CancellationToken ct = default)
        => _getAll != null
            ? _getAll(ct)
            : throw new InvalidOperationException("No getAll delegate configured.");

    public Task<CleanupResponse> CleanupMonthAsync(
        int year,
        int month,
        string deepSeekApiKey,
        CancellationToken ct = default)
        => _cleanup != null
            ? _cleanup(year, month, deepSeekApiKey, ct)
            : throw new InvalidOperationException("No cleanup delegate configured.");

    public Task<CrossCheckResponse> CrossCheckAsync(string deepSeekApiKey, CancellationToken ct = default)
        => _crossCheck != null
            ? _crossCheck(deepSeekApiKey, ct)
            : throw new InvalidOperationException("No crossCheck delegate configured.");

    public Task<List<MuxoEventDto>> GetMuxoEventsAsync(CancellationToken ct = default)
        => _getAllMuxo != null
            ? _getAllMuxo(ct)
            : throw new InvalidOperationException("No getAllMuxo delegate configured.");

    public Task<EventDetailResponse?> UpdateEventAsync(
        string eventUniqueId,
        UpdateEventRequest request,
        CancellationToken ct = default)
        => _updateEvent != null
            ? _updateEvent(eventUniqueId, request, ct)
            : throw new InvalidOperationException("No updateEvent delegate configured.");

    public Task<bool> DeleteEventAsync(string eventUniqueId, CancellationToken ct = default)
        => _deleteEvent != null
            ? _deleteEvent(eventUniqueId, ct)
            : throw new InvalidOperationException("No deleteEvent delegate configured.");
}

/// <summary>
/// Shared builders and serializers for test data.
/// </summary>
public static class TestData
{
    public static InstagramPost CreatePost(
        string postId,
        string caption = "",
        string account = "test.account",
        DateTime? datetime = null)
        => new()
        {
            Account = account,
            PostId = postId,
            Caption = caption,
            Datetime = datetime ?? new DateTime(2026, 9, 19, 20, 0, 0, DateTimeKind.Utc),
            Url = $"https://instagram.com/p/{postId}",
            ImageUrl = $"https://cdn.example.com/{postId}.jpg"
        };

    public static PostAnalysisResult EventResult(
        string title = "Concierto de prueba",
        string? eventDate = "2026-09-19T22:00:00",
        string? dateDescription = "Sábado 19 de septiembre",
        string? summary = "Gran concierto en la sala principal.")
        => new()
        {
            IsEvent = true,
            Title = title,
            EventDate = eventDate,
            EventDateDescription = dateDescription,
            Summary = summary
        };

    public static PostAnalysisResult NonEventResult()
        => new() { IsEvent = false };

    /// <summary>
    /// Weekly recurrence result, e.g. "todos los jueves" (days [4]) starting 2026-09-10.
    /// </summary>
    public static PostAnalysisResult WeeklyResult(
        List<int> daysOfWeek,
        string start,
        string? end = null,
        string title = "Evento semanal")
        => new()
        {
            IsEvent = true,
            Title = title,
            EventDate = start,
            EventDateDescription = "Todos los jueves",
            Summary = "Evento semanal recurrente",
            IsRecurrent = true,
            RecurrenceType = "weekly",
            RecurrenceDaysOfWeek = daysOfWeek,
            RecurrenceStartDate = start,
            RecurrenceEndDate = end
        };

    /// <summary>
    /// Multi-day / daily result, e.g. a fair running from start to end.
    /// </summary>
    public static PostAnalysisResult DailyRangeResult(string start, string end, string title = "Feria")
        => new()
        {
            IsEvent = true,
            Title = title,
            EventDate = start,
            EventDateDescription = $"Del {start} al {end}",
            Summary = "Feria de varios días",
            IsRecurrent = true,
            RecurrenceType = "daily",
            RecurrenceDaysOfWeek = null,
            RecurrenceStartDate = start,
            RecurrenceEndDate = end
        };

    /// <summary>
    /// Serializes the envelope DeepSeek actually returns: choices[0].message.content holds
    /// the JSON string {"results": [...]} that the service parses into BatchAnalysisResult.
    /// </summary>
    public static string BuildDeepSeekResponse(List<PostAnalysisResult> results, string finishReason = "stop")
    {
        var content = JsonSerializer.Serialize(new BatchAnalysisResult { Results = results });
        var envelope = new DeepSeekResponse
        {
            Choices = new List<DeepSeekChoice>
            {
                new()
                {
                    Message = new DeepSeekChoiceMessage { Content = content },
                    FinishReason = finishReason
                }
            }
        };
        return JsonSerializer.Serialize(envelope);
    }

    /// <summary>
    /// Serializes the DeepSeek envelope whose content holds the duplicate cleanup JSON
    /// {"duplicate_groups": [...]} that FindDuplicateEventsAsync parses.
    /// </summary>
    public static string BuildCleanupDeepSeekResponse(List<DuplicateGroupResult> groups)
    {
        var content = JsonSerializer.Serialize(new DuplicateCleanupResult { DuplicateGroups = groups });
        var envelope = new DeepSeekResponse
        {
            Choices = new List<DeepSeekChoice>
            {
                new()
                {
                    Message = new DeepSeekChoiceMessage { Content = content },
                    FinishReason = "stop"
                }
            }
        };
        return JsonSerializer.Serialize(envelope);
    }

    /// <summary>A persisted event record for service-level tests.</summary>
    public static EventRecord CreateRecord(
        string eventUniqueId,
        string title,
        string account = "test.account",
        string postId = "p-1",
        DateTime? eventDate = null,
        DateTime? recurrenceStart = null,
        DateTime? recurrenceEnd = null,
        string? recurrenceDaysOfWeek = null,
        string? recurrenceType = null,
        string? url = null)
        => new()
        {
            EventUniqueId = eventUniqueId,
            Title = title,
            Summary = $"Resumen de {title}",
            Account = account,
            PostId = postId,
            EventDate = eventDate,
            RecurrenceStartDate = recurrenceStart,
            RecurrenceEndDate = recurrenceEnd,
            RecurrenceDaysOfWeek = recurrenceDaysOfWeek,
            RecurrenceType = recurrenceType,
            IsRecurrent = recurrenceType != null,
            Url = url ?? $"https://instagram.com/p/{postId}",
            CreatedAt = DateTime.UtcNow
        };

    /// <summary>A muxojaleo event for service-level tests.</summary>
    public static MuxoEvent CreateMuxoEvent(
        string externalId,
        string title,
        DateTime? date = null,
        string? venue = null,
        string? link = null,
        string? categories = null)
        => new()
        {
            ExternalId = externalId,
            Title = title,
            Date = date,
            Venue = venue,
            Link = link,
            Categories = categories,
            CreatedAt = DateTime.UtcNow
        };

    /// <summary>
    /// Serializes the DeepSeek envelope whose content holds the cross-match JSON
    /// {"matches": [...]} that FindCrossMatchesAsync parses.
    /// </summary>
    public static string BuildCrossMatchDeepSeekResponse(List<CrossMatchResult> matches)
    {
        var content = JsonSerializer.Serialize(new CrossMatchBatchResult { Matches = matches });
        var envelope = new DeepSeekResponse
        {
            Choices = new List<DeepSeekChoice>
            {
                new()
                {
                    Message = new DeepSeekChoiceMessage { Content = content },
                    FinishReason = "stop"
                }
            }
        };
        return JsonSerializer.Serialize(envelope);
    }
}
