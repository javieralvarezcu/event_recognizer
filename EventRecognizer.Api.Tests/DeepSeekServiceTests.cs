using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EventRecognizer.Api.Models;
using EventRecognizer.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventRecognizer.Api.Tests;

public class DeepSeekServiceTests
{
    private readonly StubHttpMessageHandler _handler = new();
    private readonly TestableDeepSeekService _service;

    public DeepSeekServiceTests()
    {
        _service = new TestableDeepSeekService(
            new StubHttpClientFactory(_handler),
            NullLogger<DeepSeekService>.Instance,
            new FakeDeepSeekAuditService());
    }

    private static List<InstagramPost> CreatePosts(int count)
        => Enumerable.Range(0, count)
            .Select(i => TestData.CreatePost($"p{i}", $"post {i}"))
            .ToList();

    private static Task<HttpResponseMessage> OkResponse(List<PostAnalysisResult> results, string finishReason = "stop")
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(TestData.BuildDeepSeekResponse(results, finishReason), Encoding.UTF8, "application/json")
        });

    /// <summary>Filter (phase 1) response: one is_event flag per post, events only where listed.</summary>
    private static Task<HttpResponseMessage> OkFilterResponse(IEnumerable<int> eventIndexes, int postCount)
        => OkResponse(
            Enumerable.Range(0, postCount)
                .Select(i => new PostAnalysisResult { IsEvent = eventIndexes.Contains(i) })
                .ToList());

    /// <summary>Filter response marking every post as an event.</summary>
    private static Task<HttpResponseMessage> OkAllEventsFilter(int postCount)
        => OkFilterResponse(Enumerable.Range(0, postCount), postCount);

    /// <summary>
    /// Inspects the request body to find which post indexes were sent, and returns one
    /// result per post whose title carries that index — so alignment across chunks is
    /// verifiable end to end.
    /// </summary>
    private static Task<HttpResponseMessage> RespondWithIndexedResults(HttpRequestMessage request, string? body)
    {
        var payload = JsonSerializer.Deserialize<DeepSeekRequest>(body!)!;
        var userPrompt = payload.Messages[1].Content;

        var indexes = Regex.Matches(userPrompt, @"caption: post (\d+)")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();

        var results = indexes
            .Select(i => new PostAnalysisResult { IsEvent = true, Title = $"title-{i}" })
            .ToList();

        return OkResponse(results);
    }

    private static int CountPostsInBody(string? body)
        => Regex.Count(body ?? string.Empty, @"--- POST \d+ ---");

    [Fact]
    public async Task AnalyzePostsAsync_WithEmptyList_ReturnsEmptyWithoutCallingApi()
    {
        var results = await _service.AnalyzePostsAsync(new List<InstagramPost>(), "key");

        Assert.Empty(results);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task AnalyzePostsAsync_WithValidResponse_ReturnsParsedResults()
    {
        var posts = CreatePosts(3);
        _handler.Enqueue((_, _) => OkFilterResponse(new[] { 0, 2 }, 3));
        _handler.Enqueue((_, _) => OkResponse(new List<PostAnalysisResult>
        {
            TestData.EventResult("Fiesta A"),
            TestData.EventResult("Fiesta B")
        }));

        var results = await _service.AnalyzePostsAsync(posts, "test-api-key");

        Assert.Equal(3, results.Count);
        Assert.True(results[0].IsEvent);
        Assert.Equal("Fiesta A", results[0].Title);
        Assert.False(results[1].IsEvent);
        Assert.Equal("Fiesta B", results[2].Title);

        // Phase 1 filter over all posts, then phase 2 extraction for the 2 events.
        Assert.Equal(2, _handler.Requests.Count);
        Assert.All(_handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("api.deepseek.com", request.RequestUri!.ToString());
            Assert.Equal("Bearer test-api-key", request.Headers.Authorization!.ToString());
        });
        Assert.Contains("Clasifica las siguientes publicaciones", _handler.RequestBodies[0]);
        Assert.Contains("Analiza las siguientes publicaciones", _handler.RequestBodies[1]);
    }

    [Fact]
    public async Task AnalyzePostsAsync_With45Posts_SplitsIntoChunksAndPreservesIndexAlignment()
    {
        var posts = CreatePosts(45);
        _handler.Enqueue(RespondWithIndexedResults); // filter: one call for all 45 posts
        _handler.Enqueue(RespondWithIndexedResults); // extraction chunk 1: 30 posts
        _handler.Enqueue(RespondWithIndexedResults); // extraction chunk 2: 15 posts

        var results = await _service.AnalyzePostsAsync(posts, "key");

        Assert.Equal(posts.Count, results.Count);
        // Filter (45 in one chunk of 60) + extraction (30 + 15 per chunk).
        Assert.Equal(3, _handler.Requests.Count);
        Assert.Equal(45, CountPostsInBody(_handler.RequestBodies[0]));
        Assert.Equal(30, CountPostsInBody(_handler.RequestBodies[1]));
        Assert.Equal(15, CountPostsInBody(_handler.RequestBodies[2]));

        for (var i = 0; i < posts.Count; i++)
        {
            Assert.True(results[i].IsEvent);
            Assert.Equal($"title-{i}", results[i].Title);
        }
    }

    [Fact]
    public async Task AnalyzePostsAsync_WithMalformedJson_RetriesThenReturnsNonEvents()
    {
        var posts = CreatePosts(5);
        for (var i = 0; i < 3; i++)
            _handler.Enqueue(HttpStatusCode.OK, "this is not json");

        var results = await _service.AnalyzePostsAsync(posts, "key");

        Assert.Equal(3, _handler.Requests.Count);
        Assert.Equal(posts.Count, results.Count);
        Assert.All(results, r => Assert.False(r.IsEvent));
    }

    [Fact]
    public async Task AnalyzePostsAsync_WithTruncatedResponse_RetriesAndRecovers()
    {
        var posts = CreatePosts(5);
        _handler.Enqueue((_, _) => OkAllEventsFilter(posts.Count));
        _handler.Enqueue(HttpStatusCode.OK, TestData.BuildDeepSeekResponse(new List<PostAnalysisResult>(), finishReason: "length"));
        _handler.Enqueue((_, _) => OkResponse(
            Enumerable.Range(0, posts.Count).Select(_ => TestData.EventResult("OK")).ToList()));

        var results = await _service.AnalyzePostsAsync(posts, "key");

        Assert.Equal(3, _handler.Requests.Count);
        Assert.All(results, r => Assert.True(r.IsEvent));
    }

    [Fact]
    public async Task AnalyzePostsAsync_WithTransientHttpErrors_RetriesUntilSuccess()
    {
        var posts = CreatePosts(3);
        _handler.Enqueue(HttpStatusCode.TooManyRequests, "{}");
        _handler.Enqueue(HttpStatusCode.ServiceUnavailable, "{}");
        _handler.Enqueue((_, _) => OkAllEventsFilter(posts.Count));
        _handler.Enqueue((_, _) => OkResponse(
            Enumerable.Range(0, posts.Count).Select(_ => TestData.EventResult()).ToList()));

        var results = await _service.AnalyzePostsAsync(posts, "key");

        Assert.Equal(4, _handler.Requests.Count);
        Assert.All(results, r => Assert.True(r.IsEvent));
    }

    [Fact]
    public async Task AnalyzePostsAsync_WhenTransientErrorsExhaustRetries_Throws()
    {
        var posts = CreatePosts(3);
        for (var i = 0; i < 3; i++)
            _handler.Enqueue(HttpStatusCode.InternalServerError, "{}");

        // API unreachable: the service propagates instead of faking results.
        await Assert.ThrowsAsync<HttpRequestException>(() => _service.AnalyzePostsAsync(posts, "key"));
        Assert.Equal(3, _handler.Requests.Count);
    }

    [Fact]
    public async Task AnalyzePostsAsync_WithNonTransientHttpError_ThrowsWithoutRetrying()
    {
        var posts = CreatePosts(3);
        _handler.Enqueue(HttpStatusCode.Unauthorized, "{}");

        await Assert.ThrowsAsync<HttpRequestException>(() => _service.AnalyzePostsAsync(posts, "key"));
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task AnalyzePostsAsync_WithFewerResultsThanPosts_PadsWithNonEvents()
    {
        var posts = CreatePosts(5);
        // Filter returns only one flag for 5 posts (the rest are padded as non-events),
        // and only that one post goes to the extraction phase.
        _handler.Enqueue((_, _) => OkFilterResponse(new[] { 0 }, posts.Count));
        _handler.Enqueue((_, _) => OkResponse(new List<PostAnalysisResult>
        {
            TestData.EventResult("Solo uno")
        }));

        var results = await _service.AnalyzePostsAsync(posts, "key");

        Assert.Equal(posts.Count, results.Count);
        Assert.Equal("Solo uno", results[0].Title);
        Assert.All(results.Skip(1), r => Assert.False(r.IsEvent));
    }

    [Fact]
    public async Task AnalyzePostsAsync_WithExtraResults_IgnoresThem()
    {
        var posts = CreatePosts(3);
        _handler.Enqueue((_, _) => OkResponse(
            Enumerable.Range(0, 7).Select(_ => new PostAnalysisResult { IsEvent = true }).ToList()));
        _handler.Enqueue((_, _) => OkResponse(
            Enumerable.Range(0, 7).Select(i => TestData.EventResult($"R{i}")).ToList()));

        var results = await _service.AnalyzePostsAsync(posts, "key");

        Assert.Equal(posts.Count, results.Count);
        Assert.Equal("R0", results[0].Title);
        Assert.Equal("R2", results[2].Title);
    }

    [Fact]
    public async Task AnalyzePostsAsync_WithOneChunkFailing_ReturnsNonEventsForThatChunkOnly()
    {
        var posts = CreatePosts(35); // extraction: 30 + 5
        _handler.Enqueue(RespondWithIndexedResults); // filter: all events
        _handler.Enqueue(RespondWithIndexedResults); // extraction chunk 1 (30) succeeds
        for (var i = 0; i < 3; i++)
            _handler.Enqueue(HttpStatusCode.OK, "not json"); // chunk 2 (5) exhausts retries

        var results = await _service.AnalyzePostsAsync(posts, "key");

        Assert.Equal(posts.Count, results.Count);
        Assert.Equal(5, _handler.Requests.Count);

        for (var i = 0; i < 30; i++)
        {
            Assert.True(results[i].IsEvent);
            Assert.Equal($"title-{i}", results[i].Title);
        }
        Assert.All(results.Skip(30), r => Assert.False(r.IsEvent));
    }

    [Fact]
    public async Task AnalyzePostsAsync_WithDateRange_IncludesRangeInUserPrompt()
    {
        var posts = CreatePosts(1);
        _handler.Enqueue((_, _) => OkAllEventsFilter(posts.Count));
        _handler.Enqueue((_, _) => OkResponse(new List<PostAnalysisResult> { TestData.NonEventResult() }));

        await _service.AnalyzePostsAsync(posts, "key",
            new DateRange(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30)));

        // The range only matters for date resolution, so it goes to the extraction
        // prompt and stays out of the cheap filter.
        Assert.DoesNotContain("RANGO DE FECHAS SOLICITADO", _handler.RequestBodies[0]);
        var body = _handler.RequestBodies[1]!;
        Assert.Contains("RANGO DE FECHAS SOLICITADO", body);
        Assert.Contains("Desde: 2026-09-01", body);
        Assert.Contains("Hasta: 2026-09-30", body);
    }

    [Fact]
    public async Task AnalyzePostsAsync_WithoutDateRange_OmitsRangeFromPrompt()
    {
        var posts = CreatePosts(1);
        _handler.Enqueue((_, _) => OkAllEventsFilter(posts.Count));
        _handler.Enqueue((_, _) => OkResponse(new List<PostAnalysisResult> { TestData.NonEventResult() }));

        await _service.AnalyzePostsAsync(posts, "key");

        Assert.DoesNotContain("RANGO DE FECHAS SOLICITADO", _handler.RequestBodies[1]);
    }

    [Fact]
    public async Task AnalyzePostsAsync_WithOpenEndedRange_FormatsMissingBounds()
    {
        var posts = CreatePosts(1);
        _handler.Enqueue((_, _) => OkAllEventsFilter(posts.Count));
        _handler.Enqueue((_, _) => OkResponse(new List<PostAnalysisResult> { TestData.NonEventResult() }));

        await _service.AnalyzePostsAsync(posts, "key", new DateRange(new DateTime(2026, 9, 1), null));

        // Deserialize the payload: System.Text.Json escapes non-ASCII chars, so the
        // assertions must run on the decoded prompt text, not the raw JSON body.
        var payload = JsonSerializer.Deserialize<DeepSeekRequest>(_handler.RequestBodies[1]!)!;
        var userPrompt = payload.Messages[1].Content;
        Assert.Contains("Desde: 2026-09-01", userPrompt);
        Assert.Contains("Hasta: (sin límite)", userPrompt);
    }

    [Fact]
    public async Task AnalyzePostsAsync_SystemPrompt_InstructsRecurrenceAndNamedEventResolution()
    {
        var posts = CreatePosts(1);
        _handler.Enqueue((_, _) => OkAllEventsFilter(posts.Count));
        _handler.Enqueue((_, _) => OkResponse(new List<PostAnalysisResult> { TestData.NonEventResult() }));

        await _service.AnalyzePostsAsync(posts, "key");

        var payload = JsonSerializer.Deserialize<DeepSeekRequest>(_handler.RequestBodies[1]!)!;
        // The extraction system prompt must carry the recurrence schema, the SÍ O SÍ
        // named-event date rule and the few-shot examples — trimmed to the essentials.
        var systemPrompt = payload.Messages[0].Content;
        Assert.Contains("recurrence_days_of_week", systemPrompt);
        Assert.Contains("recurrence_type", systemPrompt);
        Assert.Contains("SÍ O SÍ", systemPrompt);
        Assert.Contains("Feria de Córdoba", systemPrompt);
        Assert.DoesNotContain("EJEMPLO 4", systemPrompt);
        Assert.DoesNotContain("REGLAS PARA DETECTAR EVENTOS", systemPrompt);
    }

    [Fact]
    public async Task AnalyzePostsAsync_FilterPrompt_IsMinimal()
    {
        var posts = CreatePosts(3);
        _handler.Enqueue((_, _) => OkAllEventsFilter(posts.Count));
        _handler.Enqueue((_, _) => OkResponse(
            Enumerable.Range(0, posts.Count).Select(_ => TestData.NonEventResult()).ToList()));

        await _service.AnalyzePostsAsync(posts, "key");

        var payload = JsonSerializer.Deserialize<DeepSeekRequest>(_handler.RequestBodies[0]!)!;
        var filterPrompt = payload.Messages[0].Content;
        Assert.Contains("is_event", filterPrompt);
        // No extraction schema, rules or examples in the cheap filter.
        Assert.DoesNotContain("recurrence_type", filterPrompt);
        Assert.DoesNotContain("EJEMPLO", filterPrompt);
        Assert.DoesNotContain("SÍ O SÍ", filterPrompt);
    }

    private static List<CleanupEventItem> CreateCleanupEvents()
        => new()
        {
            new CleanupEventItem
            {
                EventUniqueId = "EVT-1",
                Title = "Fiesta jueves",
                Summary = "Fiesta semanal",
                EventDate = new DateTime(2026, 9, 10),
                Account = "club_x",
                Caption = "Todos los jueves fiesta"
            },
            new CleanupEventItem
            {
                EventUniqueId = "EVT-2",
                Title = "Jueves de fiesta",
                Summary = "Fiesta semanal",
                Account = "club_x"
            }
        };

    [Fact]
    public async Task FindDuplicateEventsAsync_WithEmptyEvents_ReturnsEmptyWithoutCallingApi()
    {
        var groups = await _service.FindDuplicateEventsAsync(new List<CleanupEventItem>(), "2026-09", "key");

        Assert.Empty(groups);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task FindDuplicateEventsAsync_WithValidResponse_ReturnsParsedGroups()
    {
        var groups = new List<DuplicateGroupResult>
        {
            new()
            {
                KeepEventId = "EVT-1",
                DuplicateEventIds = new List<string> { "EVT-2" },
                Reason = "Mismo evento"
            }
        };
        _handler.Enqueue(HttpStatusCode.OK, TestData.BuildCleanupDeepSeekResponse(groups));

        var result = await _service.FindDuplicateEventsAsync(CreateCleanupEvents(), "2026-09", "test-api-key");

        var group = Assert.Single(result);
        Assert.Equal("EVT-1", group.KeepEventId);
        Assert.Equal("EVT-2", Assert.Single(group.DuplicateEventIds));
        Assert.Equal("Mismo evento", group.Reason);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("Bearer test-api-key", request.Headers.Authorization!.ToString());
        var body = Assert.Single(_handler.RequestBodies);
        Assert.Contains("EVT-1", body);
        Assert.Contains("2026-09", body);
    }

    [Fact]
    public async Task FindDuplicateEventsAsync_WithMalformedContent_RetriesThenThrows()
    {
        // Valid envelope, but the inner content is not parseable JSON: each attempt
        // fails to deserialize, retries are exhausted, and the caller gets an exception.
        var envelope = JsonSerializer.Serialize(new DeepSeekResponse
        {
            Choices = new List<DeepSeekChoice>
            {
                new()
                {
                    Message = new DeepSeekChoiceMessage { Content = "esto no es json" },
                    FinishReason = "stop"
                }
            }
        });
        for (var i = 0; i < 3; i++)
            _handler.Enqueue(HttpStatusCode.OK, envelope);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.FindDuplicateEventsAsync(CreateCleanupEvents(), "2026-09", "key"));
        Assert.Equal(3, _handler.Requests.Count);
    }

    [Fact]
    public async Task FindDuplicateEventsAsync_CleanupPrompts_MentionNoDateCrossing()
    {
        _handler.Enqueue(HttpStatusCode.OK, TestData.BuildCleanupDeepSeekResponse(new List<DuplicateGroupResult>()));

        await _service.FindDuplicateEventsAsync(CreateCleanupEvents(), "2026-09", "key");

        var payload = JsonSerializer.Deserialize<DeepSeekRequest>(Assert.Single(_handler.RequestBodies)!)!;
        var systemPrompt = payload.Messages[0].Content;
        Assert.Contains("duplicate_groups", systemPrompt);
        Assert.Contains("SIN FECHA", systemPrompt);
        Assert.Contains("NO te fíes solo del título", systemPrompt);
    }

    [Fact]
    public async Task FindDuplicateCandidatesAsync_WithNothingToCompare_ReturnsEmptyWithoutCallingApi()
    {
        // No candidates at all.
        var groups = await _service.FindDuplicateCandidatesAsync(
            new List<CleanupEventItem>(), CreateCleanupEvents(), "key");
        Assert.Empty(groups);
        Assert.Empty(_handler.Requests);

        // A single candidate with no existing events cannot duplicate anything.
        groups = await _service.FindDuplicateCandidatesAsync(
            CreateCleanupEvents().Take(1).ToList(), new List<CleanupEventItem>(), "key");
        Assert.Empty(groups);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task FindDuplicateCandidatesAsync_WithValidResponse_ReturnsParsedGroups()
    {
        var groups = new List<DuplicateGroupResult>
        {
            new()
            {
                KeepEventId = "EVT-1",
                DuplicateEventIds = new List<string> { "EVT-CAND-1" },
                Reason = "Misma fiesta semanal"
            }
        };
        _handler.Enqueue(HttpStatusCode.OK, TestData.BuildCleanupDeepSeekResponse(groups));

        var candidates = new List<CleanupEventItem>
        {
            new() { EventUniqueId = "EVT-CAND-1", Title = "Jueves de fiesta", Account = "club_x" }
        };

        var result = await _service.FindDuplicateCandidatesAsync(candidates, CreateCleanupEvents(), "test-api-key");

        var group = Assert.Single(result);
        Assert.Equal("EVT-1", group.KeepEventId);
        Assert.Equal("EVT-CAND-1", Assert.Single(group.DuplicateEventIds));
        Assert.Equal("Misma fiesta semanal", group.Reason);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("Bearer test-api-key", request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task FindDuplicateCandidatesAsync_Prompts_SeparateNewAndExistingLists()
    {
        _handler.Enqueue(HttpStatusCode.OK, TestData.BuildCleanupDeepSeekResponse(new List<DuplicateGroupResult>()));

        var candidates = new List<CleanupEventItem>
        {
            new() { EventUniqueId = "EVT-CAND-1", Title = "Jueves de fiesta", Account = "club_x" }
        };

        await _service.FindDuplicateCandidatesAsync(candidates, CreateCleanupEvents(), "key");

        var payload = JsonSerializer.Deserialize<DeepSeekRequest>(Assert.Single(_handler.RequestBodies)!)!;
        var userPrompt = payload.Messages[1].Content;
        Assert.Contains("EVENTOS NUEVOS", userPrompt);
        Assert.Contains("EVENTOS EXISTENTES", userPrompt);
        Assert.Contains("EVT-CAND-1", userPrompt);
        Assert.Contains("EVT-1", userPrompt);

        var systemPrompt = payload.Messages[0].Content;
        Assert.Contains("duplicate_groups", systemPrompt);
        Assert.Contains("keep_event_id SIEMPRE es el ID del evento existente", systemPrompt);
        Assert.Contains("conserva el evento nuevo", systemPrompt);
    }

    [Fact]
    public async Task FindDuplicateCandidatesAsync_WithTwoCandidatesAndNoExisting_CallsTheApi()
    {
        // Two new posts of the same event can still duplicate each other, so the
        // call happens even with no existing events.
        _handler.Enqueue(HttpStatusCode.OK, TestData.BuildCleanupDeepSeekResponse(new List<DuplicateGroupResult>()));

        var result = await _service.FindDuplicateCandidatesAsync(
            CreateCleanupEvents(), new List<CleanupEventItem>(), "key");

        Assert.Empty(result);
        Assert.Single(_handler.Requests);
    }

    private static List<MuxoEventItem> CreateMuxoItems()
        => new()
        {
            new MuxoEventItem
            {
                ExternalId = "11",
                Title = "Jam de poesía",
                Date = new DateTime(2026, 9, 9),
                Venue = "Círculo Juan 23",
                Categories = "Jam, Poesía",
                Link = "https://www.instagram.com/p/X/"
            }
        };

    [Fact]
    public async Task FindCrossMatchesAsync_WithEmptyList_ReturnsEmptyWithoutCallingApi()
    {
        var matches = await _service.FindCrossMatchesAsync(new List<CleanupEventItem>(), CreateMuxoItems(), "key");

        Assert.Empty(matches);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task FindCrossMatchesAsync_WithValidResponse_ReturnsParsedMatches()
    {
        var matches = new List<CrossMatchResult>
        {
            new() { EventUniqueId = "EVT-1", MuxoEventId = "11", Reason = "Mismo evento" }
        };
        _handler.Enqueue(HttpStatusCode.OK, TestData.BuildCrossMatchDeepSeekResponse(matches));

        var result = await _service.FindCrossMatchesAsync(CreateCleanupEvents(), CreateMuxoItems(), "test-api-key");

        var match = Assert.Single(result);
        Assert.Equal("EVT-1", match.EventUniqueId);
        Assert.Equal("11", match.MuxoEventId);
        Assert.Equal("Mismo evento", match.Reason);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("Bearer test-api-key", request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task FindCrossMatchesAsync_PromptIncludesBothLists()
    {
        _handler.Enqueue(HttpStatusCode.OK, TestData.BuildCrossMatchDeepSeekResponse(new List<CrossMatchResult>()));

        await _service.FindCrossMatchesAsync(CreateCleanupEvents(), CreateMuxoItems(), "key");

        var payload = JsonSerializer.Deserialize<DeepSeekRequest>(Assert.Single(_handler.RequestBodies)!)!;
        var userPrompt = payload.Messages[1].Content;
        Assert.Contains("NUESTROS EVENTOS", userPrompt);
        Assert.Contains("EVENTOS DE MUXOJALEO", userPrompt);
        Assert.Contains("EVT-1", userPrompt);
        Assert.Contains("11", userPrompt);
        Assert.Contains("Círculo Juan 23", userPrompt);
        // Our events must carry full context for the matching: summary, caption and
        // a humanized recurrence line (the title alone is not enough).
        Assert.Contains("resumen: Fiesta semanal", userPrompt);
        Assert.Contains("caption: Todos los jueves fiesta", userPrompt);
        Assert.Contains("recurrencia: (ninguna)", userPrompt);
        Assert.Contains("enlace del post:", userPrompt);

        var systemPrompt = payload.Messages[0].Content;
        Assert.Contains("matches", systemPrompt);
        Assert.Contains("coincidencia SEGURA", systemPrompt);
        Assert.Contains("NO exijas títulos idénticos", systemPrompt);
    }

    [Fact]
    public async Task FindCrossMatchesAsync_FormatsWeeklyRecurrenceInSpanish()
    {
        _handler.Enqueue(HttpStatusCode.OK, TestData.BuildCrossMatchDeepSeekResponse(new List<CrossMatchResult>()));

        var ourEvents = new List<CleanupEventItem>
        {
            new()
            {
                EventUniqueId = "EVT-1",
                Title = "Jueves con G",
                Summary = "Fiesta semanal",
                IsRecurrent = true,
                RecurrenceType = "weekly",
                RecurrenceDaysOfWeek = "4",
                RecurrenceStartDate = new DateTime(2026, 9, 10),
                Account = "juevescong",
                Url = "https://www.instagram.com/p/DQZPQ5zCJpa",
                Caption = "Cada jueves la misma fórmula"
            }
        };

        await _service.FindCrossMatchesAsync(ourEvents, CreateMuxoItems(), "key");

        var payload = JsonSerializer.Deserialize<DeepSeekRequest>(Assert.Single(_handler.RequestBodies)!)!;
        var userPrompt = payload.Messages[1].Content;
        Assert.Contains("recurrencia: semanal: jueves (desde 2026-09-10, sin fecha de fin)", userPrompt);
        Assert.Contains("cuenta: juevescong", userPrompt);
    }

    [Fact]
    public async Task SendChat_WithValidResponse_RecordsAuditRow()
    {
        // A non-event post stops after the cheap filter phase: one exchange, one audit row.
        var posts = CreatePosts(1);
        var usage = new DeepSeekUsage { PromptTokens = 123, CompletionTokens = 45, TotalTokens = 168 };
        _handler.Enqueue(HttpStatusCode.OK,
            TestData.BuildDeepSeekResponse(new List<PostAnalysisResult> { TestData.NonEventResult() }, usage: usage));

        await _service.AnalyzePostsAsync(posts, "super-secret-key");

        var log = Assert.Single(_service.Audit.RecordedLogs);
        Assert.Equal("analyze_posts_filter", log.Operation);
        Assert.Contains("chunk 1/1", log.ContextSummary);
        Assert.True(log.Succeeded);
        Assert.Equal(200, log.HttpStatusCode);
        Assert.Equal("stop", log.FinishReason);
        Assert.Equal(1, log.Attempt);
        Assert.Equal("deepseek-chat", log.Model);
        Assert.Equal(2048, log.MaxTokens);
        Assert.Equal(0.3, log.Temperature);

        // Tokens come from the API usage block, not the heuristic.
        Assert.Equal(123, log.PromptTokens);
        Assert.Equal(45, log.CompletionTokens);
        Assert.Equal(168, log.TotalTokens);
        Assert.False(log.TokensEstimated);

        // Prompts are stored whole, timestamps are exact and ordered.
        Assert.Contains("Clasifica las siguientes publicaciones", log.UserPrompt);
        Assert.False(string.IsNullOrEmpty(log.SystemPrompt));
        Assert.Contains("--- POST 0 ---", log.UserPrompt);
        Assert.True(log.StartedAtUtc <= log.CompletedAtUtc);
        Assert.True(log.CompletedAtUtc - log.StartedAtUtc >= TimeSpan.Zero);
        Assert.NotEqual(default, log.StartedAtUtc);

        // The API key must never end up in the audit trail.
        Assert.DoesNotContain("super-secret-key", log.SystemPrompt);
        Assert.DoesNotContain("super-secret-key", log.UserPrompt);
    }

    [Fact]
    public async Task SendChat_WithoutUsageInResponse_EstimatesTokens()
    {
        var posts = CreatePosts(1);
        _handler.Enqueue(HttpStatusCode.OK,
            TestData.BuildDeepSeekResponse(new List<PostAnalysisResult> { TestData.NonEventResult() }));

        await _service.AnalyzePostsAsync(posts, "key");

        var log = Assert.Single(_service.Audit.RecordedLogs);
        Assert.True(log.TokensEstimated);
        Assert.Equal((log.SystemPrompt.Length + log.UserPrompt.Length) / 4, log.PromptTokens);
        Assert.Equal(log.ResponseContent!.Length / 4, log.CompletionTokens);
        Assert.Equal(log.PromptTokens + log.CompletionTokens, log.TotalTokens);
    }

    [Fact]
    public async Task SendChat_WithMalformedContent_RecordsEveryFailedAttempt()
    {
        // Valid envelope but the inner content is not parseable JSON.
        var envelope = JsonSerializer.Serialize(new DeepSeekResponse
        {
            Choices = new List<DeepSeekChoice>
            {
                new()
                {
                    Message = new DeepSeekChoiceMessage { Content = "esto no es json" },
                    FinishReason = "stop"
                }
            }
        });

        var posts = CreatePosts(5);
        for (var i = 0; i < 3; i++)
            _handler.Enqueue(HttpStatusCode.OK, envelope);

        await _service.AnalyzePostsAsync(posts, "key");

        var logs = _service.Audit.RecordedLogs;
        Assert.Equal(3, logs.Count);
        for (var i = 0; i < logs.Count; i++)
        {
            var log = logs[i];
            Assert.False(log.Succeeded);
            Assert.Equal(200, log.HttpStatusCode);
            Assert.Equal("esto no es json", log.ResponseContent);
            Assert.Contains("parse", log.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(i + 1, log.Attempt);
            Assert.True(log.TokensEstimated);
            Assert.True(log.CompletionTokens > 0);
        }
    }

    [Fact]
    public async Task SendChat_WithTransientHttpError_RecordsFailedAttemptsThenThrows()
    {
        var posts = CreatePosts(3);
        for (var i = 0; i < 3; i++)
            _handler.Enqueue(HttpStatusCode.InternalServerError, "{}");

        await Assert.ThrowsAsync<HttpRequestException>(() => _service.AnalyzePostsAsync(posts, "key"));

        var logs = _service.Audit.RecordedLogs;
        Assert.Equal(3, logs.Count);
        for (var i = 0; i < logs.Count; i++)
        {
            var log = logs[i];
            Assert.False(log.Succeeded);
            Assert.Equal(500, log.HttpStatusCode);
            Assert.Null(log.ResponseContent);
            Assert.False(string.IsNullOrEmpty(log.ErrorMessage));
            Assert.Equal(i + 1, log.Attempt);
            Assert.Equal(0, log.CompletionTokens);
            Assert.True(log.PromptTokens > 0); // the request itself is still estimated
            Assert.True(log.TokensEstimated);
        }
    }

    [Fact]
    public async Task SendChat_WithNonTransientHttpError_RecordsSingleFailedAttemptThenThrows()
    {
        var posts = CreatePosts(3);
        _handler.Enqueue(HttpStatusCode.Unauthorized, "{}");

        await Assert.ThrowsAsync<HttpRequestException>(() => _service.AnalyzePostsAsync(posts, "key"));

        var log = Assert.Single(_service.Audit.RecordedLogs);
        Assert.False(log.Succeeded);
        Assert.Equal(401, log.HttpStatusCode);
        Assert.False(string.IsNullOrEmpty(log.ErrorMessage));
        Assert.Equal(1, log.Attempt);
    }
}
