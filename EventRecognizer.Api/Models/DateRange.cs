namespace EventRecognizer.Api.Models;

/// <summary>
/// Optional date range a client requests valid events for. Either bound may be null
/// (open-ended range); both null means no filter.
/// </summary>
public sealed record DateRange(DateTime? From, DateTime? To);
