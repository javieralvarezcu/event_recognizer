using System.Text.Json.Serialization;

namespace EventRecognizer.Api.Models;

/// <summary>
/// Represents the structured response from DeepSeek LLM for a single post analysis.
/// </summary>
public class PostAnalysisResult
{
    [JsonPropertyName("is_event")]
    public bool IsEvent { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("event_date")]
    public string? EventDate { get; set; }

    [JsonPropertyName("event_date_description")]
    public string? EventDateDescription { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    /// <summary>True when the event repeats over time (weekly pattern or multi-day span).</summary>
    [JsonPropertyName("is_recurrent")]
    public bool IsRecurrent { get; set; }

    /// <summary>"weekly" (repeats on weekdays), "daily" (every day within a date span) or null.</summary>
    [JsonPropertyName("recurrence_type")]
    public string? RecurrenceType { get; set; }

    /// <summary>Weekdays the event repeats on: 1 = Monday ... 7 = Sunday. E.g. "lunes a jueves" → [1,2,3,4].</summary>
    [JsonPropertyName("recurrence_days_of_week")]
    public List<int>? RecurrenceDaysOfWeek { get; set; }

    /// <summary>First day of the recurrence or multi-day span (ISO 8601).</summary>
    [JsonPropertyName("recurrence_start_date")]
    public string? RecurrenceStartDate { get; set; }

    /// <summary>Last day of the recurrence or multi-day span (ISO 8601). Null when open-ended.</summary>
    [JsonPropertyName("recurrence_end_date")]
    public string? RecurrenceEndDate { get; set; }
}

/// <summary>
/// The complete response wrapper from DeepSeek containing all post analyses.
/// </summary>
public class BatchAnalysisResult
{
    [JsonPropertyName("results")]
    public List<PostAnalysisResult> Results { get; set; } = new();
}

/// <summary>
/// A persisted event sent to the LLM for duplicate detection (month cleanup).
/// </summary>
public class CleanupEventItem
{
    /// <summary>Database unique id of the event (what the LLM must reference in its answer).</summary>
    public string EventUniqueId { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Summary { get; set; }

    public DateTime? EventDate { get; set; }

    public string? EventDateDescription { get; set; }

    public bool IsRecurrent { get; set; }

    public string? RecurrenceType { get; set; }

    /// <summary>Comma-separated weekdays as stored in the DB (1 = Monday ... 7 = Sunday).</summary>
    public string? RecurrenceDaysOfWeek { get; set; }

    public DateTime? RecurrenceStartDate { get; set; }

    public DateTime? RecurrenceEndDate { get; set; }

    public string? Account { get; set; }

    public string? Caption { get; set; }
}

/// <summary>
/// LLM response for duplicate detection: groups of events that describe the same
/// real-world event, with the id to keep and the ids to delete.
/// </summary>
public class DuplicateCleanupResult
{
    [JsonPropertyName("duplicate_groups")]
    public List<DuplicateGroupResult> DuplicateGroups { get; set; } = new();
}

public class DuplicateGroupResult
{
    /// <summary>Event id to keep (must be one of the provided ids).</summary>
    [JsonPropertyName("keep_event_id")]
    public string KeepEventId { get; set; } = string.Empty;

    /// <summary>Event ids to delete (duplicates of the kept event).</summary>
    [JsonPropertyName("duplicate_event_ids")]
    public List<string> DuplicateEventIds { get; set; } = new();

    /// <summary>Short reason in Spanish (optional).</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

// --- DeepSeek API HTTP DTOs ---

public class DeepSeekRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "deepseek-chat";

    [JsonPropertyName("messages")]
    public List<DeepSeekMessage> Messages { get; set; } = new();

    [JsonPropertyName("response_format")]
    public DeepSeekResponseFormat? ResponseFormat { get; set; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.3;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 4096;
}

public class DeepSeekMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

public class DeepSeekResponseFormat
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "json_object";
}

public class DeepSeekResponse
{
    [JsonPropertyName("choices")]
    public List<DeepSeekChoice> Choices { get; set; } = new();
}

public class DeepSeekChoice
{
    [JsonPropertyName("message")]
    public DeepSeekChoiceMessage? Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public class DeepSeekChoiceMessage
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
