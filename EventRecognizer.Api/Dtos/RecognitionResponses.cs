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
    /// <summary>Whether DeepSeek classified this post as an event.</summary>
    public bool IsEvent { get; set; }

    // --- Event fields (populated only when IsEvent == true) ---
    public string? EventUniqueId { get; set; }
    public string? Title { get; set; }
    public DateTime? EventDate { get; set; }
    public string? EventDateDescription { get; set; }
    public string? Summary { get; set; }

    // --- Recurrence fields (populated only when IsEvent == true) ---
    public bool IsRecurrent { get; set; }
    public string? RecurrenceType { get; set; }
    public string? RecurrenceDaysOfWeek { get; set; }
    public DateTime? RecurrenceStartDate { get; set; }
    public DateTime? RecurrenceEndDate { get; set; }

    // --- Original post fields (always populated) ---
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
    public bool IsRecurrent { get; set; }
    public string? RecurrenceType { get; set; }
    public string? RecurrenceDaysOfWeek { get; set; }
    public DateTime? RecurrenceStartDate { get; set; }
    public DateTime? RecurrenceEndDate { get; set; }
    public string Account { get; set; } = string.Empty;
    public string PostId { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
    public DateTime? PostDatetime { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public DateTime CreatedAt { get; set; }

    // --- Cross-match with muxojaleo.com (populated when the event is matched) ---

    /// <summary>Whether the LLM matched this event with one of muxojaleo.com.</summary>
    public bool IsCrossed { get; set; }

    public string? MuxoTitle { get; set; }

    public string? MuxoLink { get; set; }

    public DateTime? MuxoDate { get; set; }
}

/// <summary>
/// Result of a crosscheck run against the muxojaleo.com calendar.
/// </summary>
public class CrossCheckResponse
{
    /// <summary>Events scraped from muxojaleo.com (deduplicated by external id).</summary>
    public int MuxoEventsScraped { get; set; }

    /// <summary>Muxo events persisted for the first time in this run.</summary>
    public int MuxoEventsNew { get; set; }

    /// <summary>Our events sent to the LLM.</summary>
    public int OurEventsAnalyzed { get; set; }

    /// <summary>New matches persisted in this run.</summary>
    public int MatchesFound { get; set; }

    public List<CrossMatchDto> Matches { get; set; } = new();
}

public class CrossMatchDto
{
    public string EventUniqueId { get; set; } = string.Empty;

    public string? EventTitle { get; set; }

    public string? MuxoTitle { get; set; }

    public DateTime? MuxoDate { get; set; }

    public string? MuxoLink { get; set; }

    public string? Reason { get; set; }
}

public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

/// <summary>
/// Result of a month cleanup: which duplicate events were detected and removed.
/// </summary>
public class CleanupResponse
{
    /// <summary>Month cleaned, "yyyy-MM".</summary>
    public string Month { get; set; } = string.Empty;

    /// <summary>Events sent to the LLM (month events + events without any date).</summary>
    public int EventsAnalyzed { get; set; }

    /// <summary>Total events actually deleted.</summary>
    public int DeletedCount { get; set; }

    public List<CleanupGroupDto> Groups { get; set; } = new();
}

public class CleanupGroupDto
{
    public string KeepEventId { get; set; } = string.Empty;

    public string? KeepTitle { get; set; }

    /// <summary>Events actually removed in this group.</summary>
    public List<CleanupRemovedDto> Removed { get; set; } = new();

    public string? Reason { get; set; }
}

public class CleanupRemovedDto
{
    public string EventUniqueId { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Account { get; set; }
}
