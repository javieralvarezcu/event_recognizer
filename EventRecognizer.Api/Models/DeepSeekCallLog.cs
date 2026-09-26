using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EventRecognizer.Api.Models;

/// <summary>
/// Audit row for one HTTP exchange with the DeepSeek API (one attempt of one
/// LLM operation): what was sent, what came back, the tokens spent and the
/// exact timing. Never stores the API key.
/// </summary>
public class DeepSeekCallLog
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// LLM operation that produced the exchange: "analyze_posts",
    /// "cleanup_duplicates", "dedup_candidates" or "cross_match".
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Operation { get; set; } = string.Empty;

    /// <summary>
    /// Short human-readable context of the exchange (post/event counts, chunk
    /// index, month label...).
    /// </summary>
    [MaxLength(500)]
    public string? ContextSummary { get; set; }

    /// <summary>System prompt sent to the LLM.</summary>
    [Required]
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>User prompt sent to the LLM.</summary>
    [Required]
    public string UserPrompt { get; set; } = string.Empty;

    /// <summary>
    /// Raw content returned by the LLM, or the raw response body when the HTTP
    /// exchange succeeded but the envelope was malformed. Null when no response
    /// body came back (HTTP error, timeout).
    /// </summary>
    public string? ResponseContent { get; set; }

    /// <summary>Model requested in the API call.</summary>
    [Required]
    [MaxLength(50)]
    public string Model { get; set; } = string.Empty;

    /// <summary>max_tokens requested in the API call.</summary>
    public int MaxTokens { get; set; }

    /// <summary>temperature sent in the API call.</summary>
    public double Temperature { get; set; }

    /// <summary>
    /// Tokens of the request (system + user prompts). API-reported usage when the
    /// response includes it; otherwise a heuristic estimate (see
    /// <see cref="TokensEstimated"/>).
    /// </summary>
    public int PromptTokens { get; set; }

    /// <summary>
    /// Tokens of the completion. API-reported usage when the response includes it;
    /// otherwise a heuristic estimate of the response content (0 when there is none).
    /// </summary>
    public int CompletionTokens { get; set; }

    /// <summary>PromptTokens + CompletionTokens.</summary>
    public int TotalTokens { get; set; }

    /// <summary>
    /// True when the token counts were estimated from the character count
    /// (characters / 4) because the API response did not report a usage block.
    /// </summary>
    public bool TokensEstimated { get; set; }

    /// <summary>Attempt number of the exchange within its retry loop (1 = first).</summary>
    public int Attempt { get; set; }

    /// <summary>Whether the exchange produced a usable parsed result.</summary>
    public bool Succeeded { get; set; }

    /// <summary>HTTP status code of the exchange (null when no HTTP response arrived).</summary>
    public int? HttpStatusCode { get; set; }

    /// <summary>finish_reason reported by the API ("stop", "length"...).</summary>
    [MaxLength(50)]
    public string? FinishReason { get; set; }

    /// <summary>Error message when the exchange failed (parse error, HTTP error, timeout).</summary>
    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }

    /// <summary>Wall-clock duration of the exchange in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Exact UTC instant the request was sent.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>Exact UTC instant the exchange finished (success or failure).</summary>
    public DateTime CompletedAtUtc { get; set; }
}
