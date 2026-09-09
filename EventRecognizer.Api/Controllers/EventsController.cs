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
            return MissingApiKey();
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

    /// <summary>
    /// Cleans a month of duplicated events: sends the month's events (plus the events
    /// without any date) to the LLM, which decides which ones are duplicates of the
    /// same real event, and deletes them keeping one per group.
    /// </summary>
    /// <remarks>
    /// Requires the header <c>X-DeepSeek-API-Key</c> with a valid DeepSeek API token.
    /// The <c>month</c> query parameter must be in <c>yyyy-MM</c> format.
    /// </remarks>
    [HttpPost("cleanup")]
    [ProducesResponseType(typeof(CleanupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CleanupMonth([FromQuery] string? month, CancellationToken ct)
    {
        if (!TryParseMonth(month, out var year, out var monthNumber))
        {
            return BadRequest(new ErrorResponse
            {
                Error = "Invalid month",
                Detail = "The 'month' query parameter is required and must be in yyyy-MM format."
            });
        }

        var deepSeekApiKey = Request.Headers[DeepSeekApiKeyHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(deepSeekApiKey))
        {
            return MissingApiKey();
        }

        try
        {
            var result = await _eventService.CleanupMonthAsync(year, monthNumber, deepSeekApiKey, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "LLM response parsing error during cleanup");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse
            {
                Error = "Failed to process the LLM response",
                Detail = ex.Message
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "DeepSeek API call failed during cleanup");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse
            {
                Error = "Failed to communicate with the LLM service",
                Detail = ex.Message
            });
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to save cleanup results to the database");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse
            {
                Error = "Failed to save events to the database",
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Retrieves all persisted muxojaleo events with whether each one is already
    /// crossed with one of our events.
    /// </summary>
    [HttpGet("muxo")]
    [ProducesResponseType(typeof(List<MuxoEventDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllMuxo(CancellationToken ct)
    {
        var events = await _eventService.GetMuxoEventsAsync(ct);
        return Ok(events);
    }

    /// <summary>
    /// Scrapes the muxojaleo.com calendar (persisting new events) and cross-matches our
    /// persisted events against it via the LLM, storing the new matches so the panel
    /// can show which events are already registered there.
    /// </summary>
    /// <remarks>Requires the header <c>X-DeepSeek-API-Key</c> with a valid DeepSeek API token.</remarks>
    [HttpPost("crosscheck")]
    [ProducesResponseType(typeof(CrossCheckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> CrossCheck(CancellationToken ct)
    {
        var deepSeekApiKey = Request.Headers[DeepSeekApiKeyHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(deepSeekApiKey))
        {
            return MissingApiKey();
        }

        try
        {
            var result = await _eventService.CrossCheckAsync(deepSeekApiKey, ct);
            return Ok(result);
        }
        catch (ScrapeException ex)
        {
            _logger.LogError(ex, "Failed to scrape the muxojaleo calendar");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse
            {
                Error = "Failed to scrape the muxojaleo calendar",
                Detail = ex.Message
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "LLM response parsing error during crosscheck");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse
            {
                Error = "Failed to process the LLM response",
                Detail = ex.Message
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "DeepSeek API call failed during crosscheck");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResponse
            {
                Error = "Failed to communicate with the LLM service",
                Detail = ex.Message
            });
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to save crosscheck results to the database");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse
            {
                Error = "Failed to save events to the database",
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Updates the editable fields of one of our persisted events.
    /// </summary>
    /// <remarks>Requires the header <c>X-DeepSeek-API-Key</c> with a valid DeepSeek API token.</remarks>
    [HttpPut("{eventUniqueId}")]
    [ProducesResponseType(typeof(EventDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UpdateEvent(
        string eventUniqueId,
        [FromBody] UpdateEventRequest request,
        CancellationToken ct)
    {
        var deepSeekApiKey = Request.Headers[DeepSeekApiKeyHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(deepSeekApiKey))
        {
            return MissingApiKey();
        }

        try
        {
            var updated = await _eventService.UpdateEventAsync(eventUniqueId, request, ct);
            if (updated == null)
            {
                return NotFound(new ErrorResponse
                {
                    Error = "Event not found",
                    Detail = $"No event found with unique ID '{eventUniqueId}'."
                });
            }

            return Ok(updated);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to save the updated event to the database");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse
            {
                Error = "Failed to save events to the database",
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Deletes one of our persisted events (and its cross-matches).
    /// </summary>
    /// <remarks>Requires the header <c>X-DeepSeek-API-Key</c> with a valid DeepSeek API token.</remarks>
    [HttpDelete("{eventUniqueId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DeleteEvent(string eventUniqueId, CancellationToken ct)
    {
        var deepSeekApiKey = Request.Headers[DeepSeekApiKeyHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(deepSeekApiKey))
        {
            return MissingApiKey();
        }

        try
        {
            var deleted = await _eventService.DeleteEventAsync(eventUniqueId, ct);
            if (!deleted)
            {
                return NotFound(new ErrorResponse
                {
                    Error = "Event not found",
                    Detail = $"No event found with unique ID '{eventUniqueId}'."
                });
            }

            return NoContent();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to delete the event from the database");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse
            {
                Error = "Failed to save events to the database",
                Detail = ex.Message
            });
        }
    }

    private UnauthorizedObjectResult MissingApiKey()
        => Unauthorized(new ErrorResponse
        {
            Error = "Missing API key",
            Detail = $"The '{DeepSeekApiKeyHeader}' header is required with a valid DeepSeek API key."
        });

    private static bool TryParseMonth(string? month, out int year, out int monthNumber)
    {
        year = 0;
        monthNumber = 0;
        return !string.IsNullOrWhiteSpace(month)
            && DateTime.TryParseExact(month, "yyyy-MM",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed)
            && (year = parsed.Year) > 0
            && (monthNumber = parsed.Month) > 0;
    }
}
