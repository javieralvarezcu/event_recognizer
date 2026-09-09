using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EventRecognizer.Api.Models;

/// <summary>
/// A cross-match between one of our persisted events and an event of the
/// muxojaleo.com calendar, decided by the LLM during a crosscheck run.
/// </summary>
public class CrossMatch
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Our event. One match per event.</summary>
    [Required]
    [MaxLength(50)]
    public string EventUniqueId { get; set; } = string.Empty;

    /// <summary>The muxojaleo event. One match per muxo event.</summary>
    [Required]
    public int MuxoEventId { get; set; }

    [MaxLength(500)]
    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public MuxoEvent? MuxoEvent { get; set; }
}
