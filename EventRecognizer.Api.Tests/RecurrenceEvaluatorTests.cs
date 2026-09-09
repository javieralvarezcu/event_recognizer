using EventRecognizer.Api.Models;
using EventRecognizer.Api.Services;

namespace EventRecognizer.Api.Tests;

public class RecurrenceEvaluatorTests
{
    private static DateTime D(string date) => DateTime.Parse(date);

    [Fact]
    public void OccursInRange_WithNoRange_AlwaysTrue()
    {
        var analysis = new PostAnalysisResult { IsEvent = true }; // no dates at all

        Assert.True(RecurrenceEvaluator.OccursInRange(analysis, null, null));
    }

    [Fact]
    public void OccursInRange_SpecificDate_WithinRange()
    {
        var analysis = TestData.EventResult(eventDate: "2026-09-19T22:00:00");

        Assert.True(RecurrenceEvaluator.OccursInRange(analysis, D("2026-09-01"), D("2026-09-30")));
    }

    [Fact]
    public void OccursInRange_SpecificDate_OutsideRange()
    {
        var analysis = TestData.EventResult(eventDate: "2026-09-19T22:00:00");

        Assert.False(RecurrenceEvaluator.OccursInRange(analysis, D("2026-10-01"), D("2026-10-31")));
    }

    [Fact]
    public void OccursInRange_SpecificDate_OnRangeBoundary_IsInclusive()
    {
        var analysis = TestData.EventResult(eventDate: "2026-09-19T22:00:00");

        Assert.True(RecurrenceEvaluator.OccursInRange(analysis, D("2026-09-19"), D("2026-09-19")));
    }

    [Fact]
    public void OccursInRange_WithNoDatesAtAll_ReturnsFalse()
    {
        var analysis = new PostAnalysisResult { IsEvent = true };

        Assert.False(RecurrenceEvaluator.OccursInRange(analysis, D("2026-09-01"), D("2026-09-30")));
    }

    [Fact]
    public void OccursInRange_WeeklyBounded_WithinRange()
    {
        // "De lunes 14 a jueves 17 de septiembre" (14-09-2026 is a Monday).
        var analysis = TestData.WeeklyResult(
            new List<int> { 1, 2, 3, 4 }, "2026-09-14", "2026-09-17");

        Assert.True(RecurrenceEvaluator.OccursInRange(analysis, D("2026-09-10"), D("2026-09-20")));
    }

    [Fact]
    public void OccursInRange_WeeklyBounded_OutsideRange()
    {
        var analysis = TestData.WeeklyResult(
            new List<int> { 1, 2, 3, 4 }, "2026-09-14", "2026-09-17");

        Assert.False(RecurrenceEvaluator.OccursInRange(analysis, D("2026-10-01"), D("2026-10-31")));
    }

    [Fact]
    public void OccursInRange_WeeklyOpenEnded_MatchesLaterRange()
    {
        // "Todos los jueves desde el 10 de septiembre" — open-ended (2026-10-01 is a Thursday).
        var analysis = TestData.WeeklyResult(new List<int> { 4 }, "2026-09-10");

        Assert.True(RecurrenceEvaluator.OccursInRange(analysis, D("2026-10-01"), D("2026-10-31")));
    }

    [Fact]
    public void OccursInRange_WeeklyOpenEnded_BeforeStart_ReturnsFalse()
    {
        var analysis = TestData.WeeklyResult(new List<int> { 4 }, "2026-09-10");

        Assert.False(RecurrenceEvaluator.OccursInRange(analysis, D("2026-08-01"), D("2026-08-31")));
    }

    [Fact]
    public void OccursInRange_WeeklyShortWindow_WithMatchingDay()
    {
        // Range shorter than a week containing a Thursday (2026-09-17).
        var analysis = TestData.WeeklyResult(new List<int> { 4 }, "2026-09-10");

        Assert.True(RecurrenceEvaluator.OccursInRange(analysis, D("2026-09-16"), D("2026-09-17")));
    }

    [Fact]
    public void OccursInRange_WeeklyShortWindow_WithoutMatchingDay()
    {
        // Range shorter than a week without a Thursday (Fri 18 – Sat 19).
        var analysis = TestData.WeeklyResult(new List<int> { 4 }, "2026-09-10");

        Assert.False(RecurrenceEvaluator.OccursInRange(analysis, D("2026-09-18"), D("2026-09-19")));
    }

    [Fact]
    public void OccursInRange_DailyMultiDay_PartialOverlap()
    {
        // Fair from 14 to 17 September; user asks 16–20.
        var analysis = TestData.DailyRangeResult("2026-09-14", "2026-09-17");

        Assert.True(RecurrenceEvaluator.OccursInRange(analysis, D("2026-09-16"), D("2026-09-20")));
    }

    [Fact]
    public void OccursInRange_DailyMultiDay_DisjointRange()
    {
        var analysis = TestData.DailyRangeResult("2026-09-14", "2026-09-17");

        Assert.False(RecurrenceEvaluator.OccursInRange(analysis, D("2026-10-01"), D("2026-10-31")));
    }

    [Fact]
    public void OccursInRange_WithOnlyFromBound_IsOpenEnded()
    {
        var analysis = TestData.EventResult(eventDate: "2026-09-19T22:00:00");

        Assert.True(RecurrenceEvaluator.OccursInRange(analysis, D("2026-09-01"), null));
        Assert.False(RecurrenceEvaluator.OccursInRange(analysis, D("2026-09-20"), null));
    }

    [Fact]
    public void OccursInRange_WithOnlyToBound_IsOpenEnded()
    {
        var analysis = TestData.EventResult(eventDate: "2026-09-19T22:00:00");

        Assert.True(RecurrenceEvaluator.OccursInRange(analysis, null, D("2026-09-30")));
        Assert.False(RecurrenceEvaluator.OccursInRange(analysis, null, D("2026-09-18")));
    }
}
