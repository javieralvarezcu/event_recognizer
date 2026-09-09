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

        var result = await controller.Recognize(new List<InstagramPost>(), null, null, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(badRequest.Value);
        Assert.Equal("No posts provided", error.Error);
    }

    [Fact]
    public async Task Recognize_WithMissingApiKeyHeader_ReturnsUnauthorized()
    {
        var controller = CreateController(new FakeEventService());

        var result = await controller.Recognize(
            new List<InstagramPost> { TestData.CreatePost("p1") }, null, null, CancellationToken.None);

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

        var result = await controller.Recognize(posts, null, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(response, ok.Value);
        Assert.Equal("secret-key", receivedKey);
        Assert.Same(posts, receivedPosts);
        Assert.Null(service.LastDateRange);
    }

    [Fact]
    public async Task Recognize_WithDateRange_PassesRangeToService()
    {
        var service = new FakeEventService(recognize: (_, _, _) => Task.FromResult(new RecognitionResponse()));
        var controller = CreateController(service, new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });
        var dateFrom = new DateTime(2026, 9, 1);
        var dateTo = new DateTime(2026, 9, 30);

        var result = await controller.Recognize(
            new List<InstagramPost> { TestData.CreatePost("p1") }, dateFrom, dateTo, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(new DateRange(dateFrom, dateTo), service.LastDateRange);
    }

    [Fact]
    public async Task Recognize_WithOnlyDateFrom_PassesOpenEndedRange()
    {
        var service = new FakeEventService(recognize: (_, _, _) => Task.FromResult(new RecognitionResponse()));
        var controller = CreateController(service, new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });
        var dateFrom = new DateTime(2026, 9, 1);

        var result = await controller.Recognize(
            new List<InstagramPost> { TestData.CreatePost("p1") }, dateFrom, null, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(new DateRange(dateFrom, null), service.LastDateRange);
    }

    [Fact]
    public async Task Recognize_WithInvalidRange_Returns400()
    {
        var controller = CreateController(new FakeEventService(), new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });

        var result = await controller.Recognize(
            new List<InstagramPost> { TestData.CreatePost("p1") },
            new DateTime(2026, 9, 30), new DateTime(2026, 9, 1), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(badRequest.Value);
        Assert.Equal("Invalid date range", error.Error);
    }

    [Fact]
    public async Task Recognize_WhenServiceThrowsHttpRequestException_Returns502()
    {
        var service = new FakeEventService(recognize: (_, _, _) => throw new HttpRequestException("boom"));
        var controller = CreateController(service, new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });

        var result = await controller.Recognize(
            new List<InstagramPost> { TestData.CreatePost("p1") }, null, null, CancellationToken.None);

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
            new List<InstagramPost> { TestData.CreatePost("p1") }, null, null, CancellationToken.None);

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
            new List<InstagramPost> { TestData.CreatePost("p1") }, null, null, CancellationToken.None);

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

    [Fact]
    public async Task GetAll_ReturnsOkWithEventsFromService()
    {
        var events = new List<EventDetailResponse>
        {
            new() { EventUniqueId = "EVT-1", Title = "Evento uno" }
        };
        var service = new FakeEventService(getAll: _ => Task.FromResult(events));
        var controller = CreateController(service);

        var result = await controller.GetAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(events, ok.Value);
    }

    [Fact]
    public async Task GetAll_WhenServiceReturnsEmptyList_ReturnsOkWithEmptyArray()
    {
        var service = new FakeEventService(getAll: _ => Task.FromResult(new List<EventDetailResponse>()));
        var controller = CreateController(service);

        var result = await controller.GetAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var events = Assert.IsType<List<EventDetailResponse>>(ok.Value);
        Assert.Empty(events);
    }

    [Fact]
    public async Task CleanupMonth_WithMissingMonth_Returns400()
    {
        var controller = CreateController(new FakeEventService(), new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });

        var result = await controller.CleanupMonth(null, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(badRequest.Value);
        Assert.Equal("Invalid month", error.Error);
    }

    [Fact]
    public async Task CleanupMonth_WithInvalidMonth_Returns400()
    {
        var controller = CreateController(new FakeEventService(), new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });

        var result = await controller.CleanupMonth("2026-13", CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(badRequest.Value);
        Assert.Equal("Invalid month", error.Error);
    }

    [Fact]
    public async Task CleanupMonth_WithMissingApiKey_Returns401()
    {
        var controller = CreateController(new FakeEventService());

        var result = await controller.CleanupMonth("2026-09", CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(unauthorized.Value);
        Assert.Equal("Missing API key", error.Error);
    }

    [Fact]
    public async Task CleanupMonth_WithValidInput_PassesYearAndMonthToService()
    {
        int? receivedYear = null;
        int? receivedMonth = null;
        string? receivedKey = null;
        var response = new CleanupResponse { Month = "2026-09", EventsAnalyzed = 1, DeletedCount = 0 };
        var service = new FakeEventService(cleanup: (year, month, key, _) =>
        {
            receivedYear = year;
            receivedMonth = month;
            receivedKey = key;
            return Task.FromResult(response);
        });
        var controller = CreateController(service, new HeaderDictionary { ["X-DeepSeek-API-Key"] = "secret-key" });

        var result = await controller.CleanupMonth("2026-09", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(response, ok.Value);
        Assert.Equal(2026, receivedYear);
        Assert.Equal(9, receivedMonth);
        Assert.Equal("secret-key", receivedKey);
    }

    [Fact]
    public async Task CleanupMonth_WhenServiceThrowsHttpRequestException_Returns502()
    {
        var service = new FakeEventService(cleanup: (_, _, _, _) => throw new HttpRequestException("boom"));
        var controller = CreateController(service, new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });

        var result = await controller.CleanupMonth("2026-09", CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);
        var error = Assert.IsType<ErrorResponse>(objectResult.Value);
        Assert.Equal("Failed to communicate with the LLM service", error.Error);
    }

    [Fact]
    public async Task CleanupMonth_WhenServiceThrowsInvalidOperationException_Returns500()
    {
        var service = new FakeEventService(cleanup: (_, _, _, _) => throw new InvalidOperationException("malformed"));
        var controller = CreateController(service, new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });

        var result = await controller.CleanupMonth("2026-09", CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        var error = Assert.IsType<ErrorResponse>(objectResult.Value);
        Assert.Equal("Failed to process the LLM response", error.Error);
    }

    [Fact]
    public async Task GetAllMuxo_ReturnsOkWithEventsFromService()
    {
        var events = new List<MuxoEventDto>
        {
            new() { ExternalId = "20", Title = "TANZ", IsCrossed = false }
        };
        var service = new FakeEventService(getAllMuxo: _ => Task.FromResult(events));
        var controller = CreateController(service);

        var result = await controller.GetAllMuxo(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(events, ok.Value);
    }

    [Fact]
    public async Task GetAllMuxo_WhenServiceReturnsEmptyList_ReturnsOkWithEmptyArray()
    {
        var service = new FakeEventService(getAllMuxo: _ => Task.FromResult(new List<MuxoEventDto>()));
        var controller = CreateController(service);

        var result = await controller.GetAllMuxo(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var events = Assert.IsType<List<MuxoEventDto>>(ok.Value);
        Assert.Empty(events);
    }

    [Fact]
    public async Task UpdateEvent_WithMissingApiKey_Returns401()
    {
        var controller = CreateController(new FakeEventService());

        var result = await controller.UpdateEvent("EVT-1",
            new UpdateEventRequest { Title = "X", Summary = "Y" }, CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(unauthorized.Value);
        Assert.Equal("Missing API key", error.Error);
    }

    [Fact]
    public async Task UpdateEvent_WhenNotFound_Returns404()
    {
        var service = new FakeEventService(updateEvent: (_, _, _) => Task.FromResult<EventDetailResponse?>(null));
        var controller = CreateController(service, new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });

        var result = await controller.UpdateEvent("EVT-missing",
            new UpdateEventRequest { Title = "X", Summary = "Y" }, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(notFound.Value);
        Assert.Equal("Event not found", error.Error);
    }

    [Fact]
    public async Task UpdateEvent_WithValidInput_ReturnsOkWithUpdatedEvent()
    {
        string? receivedId = null;
        UpdateEventRequest? receivedRequest = null;
        var detail = new EventDetailResponse { EventUniqueId = "EVT-1", Title = "Nuevo" };
        var service = new FakeEventService(updateEvent: (id, request, _) =>
        {
            receivedId = id;
            receivedRequest = request;
            return Task.FromResult<EventDetailResponse?>(detail);
        });
        var controller = CreateController(service, new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });
        var request = new UpdateEventRequest { Title = "Nuevo", Summary = "Resumen" };

        var result = await controller.UpdateEvent("EVT-1", request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(detail, ok.Value);
        Assert.Equal("EVT-1", receivedId);
        Assert.Same(request, receivedRequest);
    }

    [Fact]
    public async Task DeleteEvent_WithMissingApiKey_Returns401()
    {
        var controller = CreateController(new FakeEventService());

        var result = await controller.DeleteEvent("EVT-1", CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(unauthorized.Value);
        Assert.Equal("Missing API key", error.Error);
    }

    [Fact]
    public async Task DeleteEvent_WhenNotFound_Returns404()
    {
        var service = new FakeEventService(deleteEvent: (_, _) => Task.FromResult(false));
        var controller = CreateController(service, new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });

        var result = await controller.DeleteEvent("EVT-missing", CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(notFound.Value);
        Assert.Equal("Event not found", error.Error);
    }

    [Fact]
    public async Task DeleteEvent_WhenDeleted_Returns204()
    {
        string? receivedId = null;
        var service = new FakeEventService(deleteEvent: (id, _) =>
        {
            receivedId = id;
            return Task.FromResult(true);
        });
        var controller = CreateController(service, new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });

        var result = await controller.DeleteEvent("EVT-1", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("EVT-1", receivedId);
    }

    [Fact]
    public async Task CrossCheck_WithMissingApiKey_Returns401()
    {
        var controller = CreateController(new FakeEventService());

        var result = await controller.CrossCheck(CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(unauthorized.Value);
        Assert.Equal("Missing API key", error.Error);
    }

    [Fact]
    public async Task CrossCheck_WithKey_ReturnsOkAndPassesKey()
    {
        string? receivedKey = null;
        var response = new CrossCheckResponse { MuxoEventsScraped = 3, MuxoEventsNew = 3, MatchesFound = 1 };
        var service = new FakeEventService(crossCheck: (key, _) =>
        {
            receivedKey = key;
            return Task.FromResult(response);
        });
        var controller = CreateController(service, new HeaderDictionary { ["X-DeepSeek-API-Key"] = "secret-key" });

        var result = await controller.CrossCheck(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(response, ok.Value);
        Assert.Equal("secret-key", receivedKey);
    }

    [Fact]
    public async Task CrossCheck_WhenServiceThrowsScrapeException_Returns502WithScrapeError()
    {
        var service = new FakeEventService(crossCheck: (_, _) =>
            throw new ScrapeException("no se pudo parsear"));
        var controller = CreateController(service, new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });

        var result = await controller.CrossCheck(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);
        var error = Assert.IsType<ErrorResponse>(objectResult.Value);
        Assert.Equal("Failed to scrape the muxojaleo calendar", error.Error);
        Assert.Equal("no se pudo parsear", error.Detail);
    }

    [Fact]
    public async Task CrossCheck_WhenServiceThrowsHttpRequestException_Returns502()
    {
        var service = new FakeEventService(crossCheck: (_, _) => throw new HttpRequestException("boom"));
        var controller = CreateController(service, new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });

        var result = await controller.CrossCheck(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);
        var error = Assert.IsType<ErrorResponse>(objectResult.Value);
        Assert.Equal("Failed to communicate with the LLM service", error.Error);
    }

    [Fact]
    public async Task CrossCheck_WhenServiceThrowsInvalidOperationException_Returns500()
    {
        var service = new FakeEventService(crossCheck: (_, _) => throw new InvalidOperationException("malformed"));
        var controller = CreateController(service, new HeaderDictionary { ["X-DeepSeek-API-Key"] = "k" });

        var result = await controller.CrossCheck(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        var error = Assert.IsType<ErrorResponse>(objectResult.Value);
        Assert.Equal("Failed to process the LLM response", error.Error);
    }
}
