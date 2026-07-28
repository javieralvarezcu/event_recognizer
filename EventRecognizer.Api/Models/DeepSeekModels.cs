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
}

/// <summary>
/// The complete response wrapper from DeepSeek containing all post analyses.
/// </summary>
public class BatchAnalysisResult
{
    [JsonPropertyName("results")]
    public List<PostAnalysisResult> Results { get; set; } = new();
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
}

public class DeepSeekChoiceMessage
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
