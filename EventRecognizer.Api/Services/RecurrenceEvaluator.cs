using EventRecognizer.Api.Models;

namespace EventRecognizer.Api.Services;

/// <summary>
/// Decides whether an event — single-date or recurrent — occurs within a requested
/// date range. Dates are compared day-granularity: an event at any time on a day
/// inside the range counts.
/// </summary>
public static class RecurrenceEvaluator
{
    /// <summary>
    /// Returns true when the analysis describes an event that occurs at least once
    /// within [from, to] (either bound may be null = open-ended). With no range at
    /// all, every event qualifies.
    /// </summary>
    public static bool OccursInRange(PostAnalysisResult analysis, DateTime? from, DateTime? to)
    {
        if (from == null && to == null)
            return true;

        var rangeFrom = from?.Date ?? DateTime.MinValue;
        var rangeTo = to?.Date ?? DateTime.MaxValue;

        var eventDate = TryParseDate(analysis.EventDate);
        var start = TryParseDate(analysis.RecurrenceStartDate);
        var end = TryParseDate(analysis.RecurrenceEndDate);

        // Single-date event (no recurrence window): only the specific date matters.
        if (start == null && end == null)
            return eventDate != null && eventDate >= rangeFrom && eventDate <= rangeTo;

        // Recurrence / multi-day span, open-ended on whichever side lacks a bound.
        var lo = start ?? rangeFrom;
        var hi = end ?? rangeTo;

        // Intersect the recurrence window with the requested range.
        if (rangeFrom > lo) lo = rangeFrom;
        if (rangeTo < hi) hi = rangeTo;
        if (lo > hi)
            return false;

        var weekdays = NormalizeWeekdays(analysis.RecurrenceDaysOfWeek);
        if (weekdays.Count > 0)
        {
            // Weekly pattern: an occurrence on each listed weekday while inside the
            // window. Any 7-day span contains every weekday, so only short windows
            // need to be walked day by day.
            if ((hi - lo).TotalDays >= 6)
                return true;

            for (var day = lo; day <= hi; day = day.AddDays(1))
            {
                if (weekdays.Contains(day.DayOfWeek))
                    return true;
            }

            return false;
        }

        // Daily pattern or multi-day event: any overlap with the window counts.
        return true;
    }

    private static DateTime? TryParseDate(string? value)
        => DateTime.TryParse(value, out var parsed) ? parsed.Date : null;

    private static HashSet<DayOfWeek> NormalizeWeekdays(List<int>? days)
    {
        var result = new HashSet<DayOfWeek>();
        if (days == null)
            return result;

        foreach (var day in days)
        {
            if (day is >= 1 and <= 7) // 1 = Monday ... 7 = Sunday
                result.Add((DayOfWeek)(day - 1));
        }

        return result;
    }
}
