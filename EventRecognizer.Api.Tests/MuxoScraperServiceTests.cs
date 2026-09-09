using System.Net;
using System.Text;
using System.Text.Json;
using EventRecognizer.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventRecognizer.Api.Tests;

public class MuxoScraperServiceTests
{
    private readonly StubHttpMessageHandler _handler = new();
    private readonly MuxoScraperService _service;

    public MuxoScraperServiceTests()
    {
        _service = new MuxoScraperService(
            new StubHttpClientFactory(_handler),
            NullLogger<MuxoScraperService>.Instance);
    }

    /// <summary>
    /// Builds an HTML snippet with the astro-island props in the seroval format the
    /// real site uses: [0, x] wraps a value, [1, [...]] is an array of wrapped items.
    /// </summary>
    private static string BuildHtml(params (string Id, string? Title, string? Date, string? Venue, string? Link)[] events)
    {
        var serializedEvents = events.Select(e => (object)new object?[]
        {
            0,
            new Dictionary<string, object?>
            {
                ["id"] = new object?[] { 0, e.Id },
                ["title"] = new object?[] { 0, e.Title },
                ["date"] = new object?[] { 0, e.Date },
                ["location"] = new object?[] { 0, new Dictionary<string, object?>
                {
                    ["venue"] = new object?[] { 0, e.Venue }
                } },
                ["link"] = new object?[] { 0, e.Link },
                ["categories"] = new object?[]
                {
                    1,
                    new object?[]
                    {
                        new object?[] { 0, new Dictionary<string, object?> { ["title"] = new object?[] { 0, "Jam" } } },
                        new object?[] { 0, new Dictionary<string, object?> { ["title"] = new object?[] { 0, "Poesía" } } }
                    }
                },
                ["price"] = new object?[] { 0, "Gratis" }
            }
        }).ToArray();

        var props = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["mode"] = new object[] { 0, "month" },
            ["events"] = new object[] { 1, serializedEvents }
        });

        return $"<astro-island uid=\"abc\" prefix=\"r2\" component-url=\"/_astro/Calendar.D5rejZOb.js\" " +
               $"component-export=\"default\" renderer-url=\"/_astro/client.js\" props=\"{System.Net.WebUtility.HtmlEncode(props)}\"></astro-island>";
    }

    [Fact]
    public void ParseEvents_ExtractsEventsFromAstroIslandProps()
    {
        var html = BuildHtml(
            ("11", "Jam de poesía", "2026-09-09T12:00:00.000Z", "Círculo Juan 23", "https://www.instagram.com/p/X/"),
            ("12", "Concierto", "2026-09-11T12:00:00.000Z", "Sala X", "https://www.instagram.com/p/Y/"));

        var events = MuxoScraperService.ParseEvents(html);

        Assert.Equal(2, events.Count);
        var first = events[0];
        Assert.Equal("11", first.ExternalId);
        Assert.Equal("Jam de poesía", first.Title);
        Assert.Equal(DateTime.Parse("2026-09-09T12:00:00.000Z"), first.Date);
        Assert.Equal("Círculo Juan 23", first.Venue);
        Assert.Equal("https://www.instagram.com/p/X/", first.Link);
        Assert.Equal("Jam, Poesía", first.Categories);
        Assert.Equal("Gratis", first.Price);

        Assert.Equal("12", events[1].ExternalId);
        Assert.Equal("Concierto", events[1].Title);
    }

    [Fact]
    public void ParseEvents_IgnoresEntriesWithoutIdOrTitle()
    {
        var html = BuildHtml(
            ("11", null, null, null, null),            // without title: skipped
            ("12", "Concierto", "2026-09-11T12:00:00.000Z", "Sala X", null));

        var events = MuxoScraperService.ParseEvents(html);

        var evt = Assert.Single(events);
        Assert.Equal("12", evt.ExternalId);
    }

    [Fact]
    public void ParseEvents_WithMissingIsland_ThrowsScrapeException()
    {
        var ex = Assert.Throws<ScrapeException>(() => MuxoScraperService.ParseEvents("<html>sin calendario</html>"));
        Assert.Contains("No se encontró el calendario", ex.Message);
    }

    [Fact]
    public void ParseEvents_WithMalformedPropsJson_ThrowsScrapeException()
    {
        var html = "<astro-island uid=\"abc\" component-url=\"/_astro/Calendar.D5rejZOb.js\" props=\"esto no es json\"></astro-island>";
        var ex = Assert.Throws<ScrapeException>(() => MuxoScraperService.ParseEvents(html));
        Assert.Contains("no son JSON válido", ex.Message);
    }

    [Fact]
    public async Task ScrapeMonthAsync_RequestsTheMonthUrl()
    {
        _handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildHtml(("11", "Jam", "2026-09-09T12:00:00.000Z", null, null)),
                Encoding.UTF8, "text/html")
        }));

        var events = await _service.ScrapeMonthAsync("2026-09");

        Assert.Single(events);
        var request = Assert.Single(_handler.Requests);
        Assert.Contains("/calendario?month=2026-09", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task ScrapeMonthAsync_WithHttpError_ThrowsScrapeException()
    {
        _handler.Enqueue(HttpStatusCode.InternalServerError, "error");

        var ex = await Assert.ThrowsAsync<ScrapeException>(() => _service.ScrapeMonthAsync("2026-09"));
        Assert.Contains("HTTP 500", ex.Message);
    }

    [Fact]
    public async Task ScrapeUpcomingAsync_DeduplicatesByExternalIdAcrossMonths()
    {
        // The same event appears on every month page it touches.
        var html = BuildHtml(("11", "Jam de poesía", "2026-09-09T12:00:00.000Z", null, null));
        _handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        }));
        _handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        }));
        _handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        }));

        var events = await _service.ScrapeUpcomingAsync(2);

        Assert.Equal(3, _handler.Requests.Count); // current month + 2
        Assert.Single(events);
    }
}
