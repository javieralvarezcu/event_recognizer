using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EventRecognizer.Api.Models;

/// <summary>
/// Cache of the LLM analysis of one Instagram post, keyed by the content hash of
/// the post (account + caption + datetime + url + requested date range). Posts
/// already analyzed are never sent to the LLM again, whether they were events
/// or not. Used to stop repeated submissions of the same dataset from burning
/// tokens on re-analysis.
/// </summary>
public class PostAnalysisCache
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>SHA-256 hex of the post content fingerprint (see PostAnalysisCacheService).</summary>
    [Required]
    [MaxLength(64)]
    public string PostHash { get; set; } = string.Empty;

    /// <summary>PostId of the analyzed post, for diagnostics.</summary>
    [Required]
    [MaxLength(100)]
    public string PostId { get; set; } = string.Empty;

    /// <summary>Serialized <see cref="PostAnalysisResult"/> of the post.</summary>
    [Required]
    public string AnalysisJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
