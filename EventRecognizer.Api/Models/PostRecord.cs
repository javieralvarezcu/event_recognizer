using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EventRecognizer.Api.Models;

/// <summary>
/// Every Instagram post received through the recognition endpoints, keyed by its
/// URL. Stores the analysis outcome once known — event or not — with the full
/// analysis JSON. Posts whose URL is already here are never sent to the LLM again,
/// whether they were events or not: the LLM only sees URLs the system does not know.
/// </summary>
public class PostRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>URL of the Instagram post — the identity of a post in the registry.</summary>
    [Required]
    [MaxLength(500)]
    public string Url { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Account { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string PostId { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string Caption { get; set; } = string.Empty;

    public DateTime? PostDatetime { get; set; }

    [MaxLength(1000)]
    public string? ImageUrl { get; set; }

    /// <summary>Whether the post was analyzed as an event (only meaningful once <see cref="AnalysisJson"/> is set).</summary>
    public bool IsEvent { get; set; }

    /// <summary>
    /// Serialized <see cref="PostAnalysisResult"/>. Null until the post has been
    /// analyzed; rows with a null analysis are re-analyzed by the next request.
    /// </summary>
    public string? AnalysisJson { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? AnalyzedAtUtc { get; set; }
}
