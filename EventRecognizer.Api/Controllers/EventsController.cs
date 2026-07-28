using EventRecognizer.Api.Dtos;
using EventRecognizer.Api.Models;
using EventRecognizer.Api.Services;
using Microsoft.AspNetCore.Mvc;

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
    /// </remarks>
    [HttpPost("recognize")]
    [ProducesResponseType(typeof(RecognitionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Recognize([FromBody] List<InstagramPost> posts, CancellationToken ct)
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

        try
        {
            var result = await _eventService.RecognizeEventsAsync(posts, deepSeekApiKey, ct);
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
}
