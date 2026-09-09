using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EventRecognizer.Api.Models;

/// <summary>
/// Event scraped from the muxojaleo.com calendar. Persisted so the site is only
/// scraped (and the LLM only called) when the user clicks the crosscheck button.
/// </summary>
public class MuxoEvent
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// Event id assigned by muxojaleo.com (unique). Used to avoid persisting duplicates.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string ExternalId { get; set; } = string.Empty;

    [Required]
    [MaxLength(300)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Event date as published on the site.</summary>
    public DateTime? Date { get; set; }

    [MaxLength(200)]
    public string? Venue { get; set; }

    /// <summary>Link published with the event (usually an Instagram post).</summary>
    [MaxLength(500)]
    public string? Link { get; set; }

    /// <summary>Comma-separated category titles as published on the site.</summary>
    [MaxLength(200)]
    public string? Categories { get; set; }

    [MaxLength(100)]
    public string? Price { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
