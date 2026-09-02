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
            NullLogger<DeepSeekService>.Instance);
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
        _handler.Enqueue((_, _) => OkResponse(new List<PostAnalysisResult>
        {
            TestData.EventResult("Fiesta A"),
            TestData.NonEventResult(),
            TestData.EventResult("Fiesta B")
        }));

        var results = await _service.AnalyzePostsAsync(posts, "test-api-key");

        Assert.Equal(3, results.Count);
        Assert.True(results[0].IsEvent);
        Assert.Equal("Fiesta A", results[0].Title);
        Assert.False(results[1].IsEvent);
        Assert.Equal("Fiesta B", results[2].Title);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("api.deepseek.com", request.RequestUri!.ToString());
        Assert.Equal("Bearer test-api-key", request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task AnalyzePostsAsync_With45Posts_SplitsIntoChunksAndPreservesIndexAlignment()
    {
        var posts = CreatePosts(45);
        for (var i = 0; i < 3; i++) // one queued response per chunk (20 + 20 + 5)
            _handler.Enqueue(RespondWithIndexedResults);

        var results = await _service.AnalyzePostsAsync(posts, "key");

        Assert.Equal(posts.Count, results.Count);
        // 20 posts per chunk: 20 + 20 + 5
        Assert.Equal(3, _handler.Requests.Count);
        Assert.Equal(20, CountPostsInBody(_handler.RequestBodies[0]));
        Assert.Equal(20, CountPostsInBody(_handler.RequestBodies[1]));
        Assert.Equal(5, CountPostsInBody(_handler.RequestBodies[2]));

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
        _handler.Enqueue(HttpStatusCode.OK, TestData.BuildDeepSeekResponse(new List<PostAnalysisResult>(), finishReason: "length"));
        _handler.Enqueue((_, _) => OkResponse(
            Enumerable.Range(0, posts.Count).Select(_ => TestData.EventResult("OK")).ToList()));

        var results = await _service.AnalyzePostsAsync(posts, "key");

        Assert.Equal(2, _handler.Requests.Count);
        Assert.All(results, r => Assert.True(r.IsEvent));
    }

    [Fact]
    public async Task AnalyzePostsAsync_WithTransientHttpErrors_RetriesUntilSuccess()
    {
        var posts = CreatePosts(3);
        _handler.Enqueue(HttpStatusCode.TooManyRequests, "{}");
        _handler.Enqueue(HttpStatusCode.ServiceUnavailable, "{}");
        _handler.Enqueue((_, _) => OkResponse(
            Enumerable.Range(0, posts.Count).Select(_ => TestData.EventResult()).ToList()));

        var results = await _service.AnalyzePostsAsync(posts, "key");

        Assert.Equal(3, _handler.Requests.Count);
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
        var extras = Enumerable.Range(0, 7).Select(i => TestData.EventResult($"R{i}")).ToList();
        _handler.Enqueue((_, _) => OkResponse(extras));

        var results = await _service.AnalyzePostsAsync(posts, "key");

        Assert.Equal(posts.Count, results.Count);
        Assert.Equal("R0", results[0].Title);
        Assert.Equal("R2", results[2].Title);
    }

    [Fact]
    public async Task AnalyzePostsAsync_WithOneChunkFailing_ReturnsNonEventsForThatChunkOnly()
    {
        var posts = CreatePosts(25); // 20 + 5
        _handler.Enqueue(RespondWithIndexedResults); // first chunk succeeds
        for (var i = 0; i < 3; i++)
            _handler.Enqueue(HttpStatusCode.OK, "not json"); // second chunk exhausts retries

        var results = await _service.AnalyzePostsAsync(posts, "key");

        Assert.Equal(posts.Count, results.Count);
        Assert.Equal(4, _handler.Requests.Count);

        for (var i = 0; i < 20; i++)
        {
            Assert.True(results[i].IsEvent);
            Assert.Equal($"title-{i}", results[i].Title);
        }
        Assert.All(results.Skip(20), r => Assert.False(r.IsEvent));
    }
}
