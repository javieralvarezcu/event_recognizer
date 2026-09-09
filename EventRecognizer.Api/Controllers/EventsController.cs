using EventRecognizer.Api.Dtos;
using EventRecognizer.Api.Models;
using EventRecognizer.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventRecognizer.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EventsController : ControllerBase
{
    private readonly IEventService _eventService;
    private readonly ILogger<EventsController> _logger;
    private const string DeepSeekApiKeyHeader = "X-DeepSeek-API-Key";

    public EventsController(IEventService eventService, ILogger<EventsController> logger)
    {
        _eventService = eventService;
        _logger = logger;
    }

    /// <summary>
    /// Receives a list of Instagram posts, recognizes which ones are event posters,
    /// and returns the structured event information.
    /// </summary>
    /// <remarks>
    /// Requires the header <c>X-DeepSeek-API-Key</c> with a valid DeepSeek API token.
    /// Optionally accepts <c>dateFrom</c>/<c>dateTo</c> query parameters with the date range
    /// of valid events: events (including recurring ones) outside the range are returned
    /// as non-events and are not persisted.
    /// </remarks>
    [HttpPost("recognize")]
    [ProducesResponseType(typeof(RecognitionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Recognize(
        [FromBody] List<InstagramPost> posts,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        CancellationToken ct = default)
    {
        if (posts == null || posts.Count == 0)
        {
            return BadRequest(new ErrorResponse
            {
                Error = "No posts provided",
                Detail = "The request body must be a non-empty array of Instagram posts."
            });
        }

        var deepSeekApiKey = Request.Headers[DeepSeekApiKeyHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(deepSeekApiKey))
        {
            return Unauthorized(new ErrorResponse
            {
                Error = "Missing API key",
                Detail = $"The '{DeepSeekApiKeyHeader}' header is required with a valid DeepSeek API key."
            });
        }

        if (dateFrom.HasValue && dateTo.HasValue && dateFrom > dateTo)
        {
            return BadRequest(new ErrorResponse
            {
                Error = "Invalid date range",
                Detail = "'dateFrom' must be earlier than or equal to 'dateTo'."
            });
        }

        var dateRange = dateFrom.HasValue || dateTo.HasValue
            ? new DateRange(dateFrom, dateTo)
            : null;

        try
        {
            var result = await _eventService.RecognizeEventsAsync(posts, deepSeekApiKey, dateRange, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "LLM response parsing error");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse
            {
                Error = "Failed to process the LLM response",
                Detail = ex.Message
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "DeepSeek API call failed");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse
            {
                Error = "Failed to communicate with the LLM service",
                Detail = ex.Message
            });
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to save events to the database");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse
            {
                Error = "Failed to save events to the database",
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Retrieves a previously recognized event by its unique event identifier.
    /// </summary>
    [HttpGet("{eventUniqueId}")]
    [ProducesResponseType(typeof(EventDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByUniqueId(string eventUniqueId, CancellationToken ct)
    {
        var result = await _eventService.GetEventByUniqueIdAsync(eventUniqueId, ct);

        if (result == null)
        {
            return NotFound(new ErrorResponse
            {
                Error = "Event not found",
                Detail = $"No event found with unique ID '{eventUniqueId}'."
            });
        }

        return Ok(result);
    }

    /// <summary>
    /// Retrieves all persisted events, ordered by effective date (oldest first, events
    /// without a computable date last).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<EventDetailResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var events = await _eventService.GetAllEventsAsync(ct);
        return Ok(events);
    }
}
