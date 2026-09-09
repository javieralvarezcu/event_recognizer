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

    public FakeDeepSeekService(Func<List<InstagramPost>, string, List<PostAnalysisResult>> analyze)
        => _analyze = analyze;

    public List<string> ReceivedApiKeys { get; } = new();

    public DateRange? LastDateRange { get; private set; }

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
}

/// <summary>
/// IEventService fake driven by delegates for the three endpoints.
/// </summary>
public sealed class FakeEventService : IEventService
{
    private readonly Func<List<InstagramPost>, string, CancellationToken, Task<RecognitionResponse>>? _recognize;
    private readonly Func<string, CancellationToken, Task<EventDetailResponse?>>? _getByUniqueId;
    private readonly Func<CancellationToken, Task<List<EventDetailResponse>>>? _getAll;

    public FakeEventService(
        Func<List<InstagramPost>, string, CancellationToken, Task<RecognitionResponse>>? recognize = null,
        Func<string, CancellationToken, Task<EventDetailResponse?>>? getByUniqueId = null,
        Func<CancellationToken, Task<List<EventDetailResponse>>>? getAll = null)
    {
        _recognize = recognize;
        _getByUniqueId = getByUniqueId;
        _getAll = getAll;
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
}
