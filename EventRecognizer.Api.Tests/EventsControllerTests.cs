using EventRecognizer.Api.Controllers;
using EventRecognizer.Api.Dtos;
using EventRecognizer.Api.Models;
using EventRecognizer.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventRecognizer.Api.Tests;

public class EventsControllerTests
{
    private static EventsController CreateController(IEventService service, HeaderDictionary? headers = null)
    {
        var controller = new EventsController(service, NullLogger<EventsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        if (headers != null)
        {
            foreach (var (key, value) in headers)
                controller.HttpContext.Request.Headers[key] = value;
        }

        return controller;
    }

    [Fact]
    public async Task Recognize_WithEmptyPosts_ReturnsBadRequest()
    {
        var controller = CreateController(new FakeEventService());

        var result = await controller.Recognize(new List<InstagramPost>(), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(badRequest.Value);
        Assert.Equal("No posts provided", error.Error);
    }

    [Fact]
    public async Task Recognize_WithMissingApiKeyHeader_ReturnsUnauthorized()
    {
        var controller = CreateController(new FakeEventService());

        var result = await controller.Recognize(
            new List<InstagramPost> { TestData.CreatePost("p1") }, CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(unauthorized.Value);
        Assert.Equal("Missing API key", error.Error);
    }

    [Fact]
    public async Task Recognize_WithKeyAndPosts_ReturnsOkAndPassesThemThrough()
    {
        string? receivedKey = null;
        List<InstagramPost>? receivedPosts = null;
        var response = new RecognitionResponse { TotalPosts = 1, EventsFound = 0, Events = new List<RecognizedEventDto>() };
        var service = new FakeEventService(recognize: (posts, key, _) =>
        {
            receivedKey = key;
            receivedPosts = posts;
            return Task.FromResult(response);
        });
        var controller = CreateController(service, new HeaderDictionary
        {
            ["X-DeepSeek-API-Key"] = "secret-key"
        });
        var posts = new List<InstagramPost> { TestData.CreatePost("p1") };

        var result = await controller.Recognize(posts, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(response, ok.Value);
        Assert.Equal("secret-key", receivedKey);
        Assert.Same(posts, receivedPosts);
    }

    [Fact]
    public async Task Recognize_WhenServiceThrowsHttpRequestException_Returns502()
    {
        var service = new FakeEventService(recognize: (_, _, _) => throw new HttpRequestException("boom"));
        var controller = CreateController(service, new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });

        var result = await controller.Recognize(
            new List<InstagramPost> { TestData.CreatePost("p1") }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);
        var error = Assert.IsType<ErrorResponse>(objectResult.Value);
        Assert.Equal("Failed to communicate with the LLM service", error.Error);
    }

    [Fact]
    public async Task Recognize_WhenServiceThrowsInvalidOperationException_Returns500()
    {
        var service = new FakeEventService(recognize: (_, _, _) => throw new InvalidOperationException("malformed"));
        var controller = CreateController(service, new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });

        var result = await controller.Recognize(
            new List<InstagramPost> { TestData.CreatePost("p1") }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        var error = Assert.IsType<ErrorResponse>(objectResult.Value);
        Assert.Equal("Failed to process the LLM response", error.Error);
    }

    [Fact]
    public async Task Recognize_WhenServiceThrowsDbUpdateException_Returns500()
    {
        var service = new FakeEventService(recognize: (_, _, _) =>
            throw new DbUpdateException("db boom", (Exception?)null));
        var controller = CreateController(service, new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });

        var result = await controller.Recognize(
            new List<InstagramPost> { TestData.CreatePost("p1") }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        var error = Assert.IsType<ErrorResponse>(objectResult.Value);
        Assert.Equal("Failed to save events to the database", error.Error);
    }

    [Fact]
    public async Task GetByUniqueId_WhenNotFound_Returns404()
    {
        var service = new FakeEventService(getByUniqueId: (_, _) => Task.FromResult<EventDetailResponse?>(null));
        var controller = CreateController(service);

        var result = await controller.GetByUniqueId("EVT-missing", CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(notFound.Value);
        Assert.Equal("Event not found", error.Error);
    }

    [Fact]
    public async Task GetByUniqueId_WhenFound_ReturnsOk()
    {
        var detail = new EventDetailResponse { EventUniqueId = "EVT-1", Title = "Evento uno" };
        var service = new FakeEventService(getByUniqueId: (id, _) =>
        {
            Assert.Equal("EVT-1", id);
            return Task.FromResult<EventDetailResponse?>(detail);
        });
        var controller = CreateController(service);

        var result = await controller.GetByUniqueId("EVT-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(detail, ok.Value);
    }
}
