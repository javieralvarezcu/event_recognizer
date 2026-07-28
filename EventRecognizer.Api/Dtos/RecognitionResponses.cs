namespace EventRecognizer.Api.Dtos;

/// <summary>
/// Response returned by the POST /api/events/recognize endpoint.
/// </summary>
public class RecognitionResponse
{
    public int TotalPosts { get; set; }
    public int EventsFound { get; set; }
    public List<RecognizedEventDto> Events { get; set; } = new();
}

public class RecognizedEventDto
{
    public string EventUniqueId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime? EventDate { get; set; }
    public string? EventDateDescription { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public string PostId { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
    public DateTime? PostDatetime { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class EventDetailResponse
{
    public string EventUniqueId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime? EventDate { get; set; }
    public string? EventDateDescription { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public string PostId { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
    public DateTime? PostDatetime { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
    public string? Detail { get; set; }
}
