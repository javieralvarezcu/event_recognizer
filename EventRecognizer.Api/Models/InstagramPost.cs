using System.Text.Json.Serialization;

namespace EventRecognizer.Api.Models;

/// <summary>
/// Represents an Instagram post submitted for recognition.
/// </summary>
public class InstagramPost
{
    [JsonPropertyName("account")]
    public string Account { get; set; } = string.Empty;

    [JsonPropertyName("post_id")]
    public string PostId { get; set; } = string.Empty;

    /// <summary>
    /// Nullable: Instagram posts without a caption are common (e.g. reels).
    /// Making it nullable also prevents the implicit [Required] that ASP.NET Core
    /// applies to non-nullable reference types from rejecting such posts.
    /// </summary>
    [JsonPropertyName("caption")]
    public string? Caption { get; set; }

    [JsonPropertyName("datetime")]
    public DateTime? Datetime { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }
}
