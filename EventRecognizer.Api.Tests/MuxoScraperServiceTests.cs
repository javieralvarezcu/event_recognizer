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
    /// Builds the /eventos page HTML: an EventsGrid astro-island whose props carry the
    /// events in the seroval format the real site uses ([0, x] wraps a value,
    /// [1, [...]] is an array of wrapped items).
    /// </summary>
    private static string BuildGridHtml(params (string Id, string? Title, string? Date, string? Venue, string? Link)[] events)
    {
        var serializedEvents = events.Select(e => (object)new object?[]
        {
            0,
            new Dictionary<string, object?>
            {
                ["id"] = new object?[] { 0, e.Id },
                ["title"] = new object?[] { 0, e.Title },
                ["date"] = new object?[] { 0, e.Date },
                ["venue"] = new object?[] { 0, e.Venue },
                ["link"] = new object?[] { 0, e.Link },
                ["categories"] = new object?[]
                {
                    1,
                    new object?[]
                    {
                        new object?[] { 0, new Dictionary<string, object?> { ["title"] = new object?[] { 0, "Jam" } } },
                        new object?[] { 0, new Dictionary<string, object?> { ["title"] = new object?[] { 0, "Poesía" } } }
                    }
                }
            }
        }).ToArray();

        var props = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["initialEvents"] = new object?[] { 1, serializedEvents },
            ["pageSize"] = 24
        });

        return "<astro-island uid=\"abc\" prefix=\"r2\" component-url=\"/_astro/EventsGrid.6gqUT4Pn.js\" " +
               "component-export=\"default\" renderer-url=\"/_astro/client.js\" props=\"" +
               System.Net.WebUtility.HtmlEncode(props) + "\"></astro-island>";
    }

    private static string BuildApiJson(params (int Id, string? Title, string? Date, string? Link, string? Price)[] events)
        => JsonSerializer.Serialize(events.Select(e => new
        {
            id = e.Id,
            title = e.Title,
            date = e.Date,
            link = e.Link,
            price = e.Price,
            categories = new[] { 4 }
        }));

    private static Task<HttpResponseMessage> HtmlResponse(string html)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        });

    private static Task<HttpResponseMessage> JsonResponse(string json)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

    [Fact]
    public void ParseEventsGrid_ExtractsRichEventsFromAstroIslandProps()
    {
        var html = BuildGridHtml(
            ("11", "Jam de poesía", "2026-09-09T12:00:00.000Z", "Círculo Juan 23", "https://www.instagram.com/p/X/"),
            ("12", "Concierto", "2026-09-11T12:00:00.000Z", "Sala X", "https://www.instagram.com/p/Y/"));

        var events = MuxoScraperService.ParseEventsGrid(html);

        Assert.Equal(2, events.Count);
        var first = events[0];
        Assert.Equal("11", first.ExternalId);
        Assert.Equal("Jam de poesía", first.Title);
        Assert.Equal(DateTime.Parse("2026-09-09T12:00:00.000Z"), first.Date);
        Assert.Equal("Círculo Juan 23", first.Venue);
        Assert.Equal("https://www.instagram.com/p/X/", first.Link);
        Assert.Equal("Jam, Poesía", first.Categories);

        Assert.Equal("12", events[1].ExternalId);
        Assert.Equal("Concierto", events[1].Title);
    }

    [Fact]
    public void ParseEventsGrid_IgnoresEntriesWithoutIdOrTitle()
    {
        var html = BuildGridHtml(
            ("11", null, null, null, null),
            ("12", "Concierto", "2026-09-11T12:00:00.000Z", "Sala X", null));

        var events = MuxoScraperService.ParseEventsGrid(html);

        var evt = Assert.Single(events);
        Assert.Equal("12", evt.ExternalId);
    }

    [Fact]
    public void ParseEventsGrid_WithMissingGrid_ThrowsScrapeException()
    {
        var ex = Assert.Throws<ScrapeException>(() => MuxoScraperService.ParseEventsGrid("<html>sin listado</html>"));
        Assert.Contains("No se encontró el listado", ex.Message);
    }

    [Fact]
    public void ParseEventsGrid_WithMalformedPropsJson_ThrowsScrapeException()
    {
        var html = "<astro-island uid=\"abc\" component-url=\"/_astro/EventsGrid.6gqUT4Pn.js\" props=\"esto no es json\"></astro-island>";
        var ex = Assert.Throws<ScrapeException>(() => MuxoScraperService.ParseEventsGrid(html));
        Assert.Contains("no son JSON válido", ex.Message);
    }

    [Fact]
    public async Task FetchApiEventsAsync_ParsesJsonArray()
    {
        _handler.Enqueue((_, _) => JsonResponse(BuildApiJson(
            (11, "Jam de poesía", "2026-09-09T12:00:00.000Z", "https://www.instagram.com/p/X/", "Gratis"),
            (20, "TANZ", "2026-09-25T20:00:00.000Z", null, null))));

        var events = await _service.FetchApiEventsAsync("2026-08-31", "2026-10-04");

        Assert.Equal(2, events.Count);
        var first = events[0];
        Assert.Equal("11", first.ExternalId);
        Assert.Equal("Jam de poesía", first.Title);
        Assert.Equal("Gratis", first.Price);
        Assert.Equal("https://www.instagram.com/p/X/", first.Link);
        Assert.Null(first.Venue); // la API solo trae el id del lugar

        var request = Assert.Single(_handler.Requests);
        Assert.Contains("/api/events?from=2026-08-31&to=2026-10-04", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task FetchApiEventsAsync_WithMalformedJson_ThrowsScrapeException()
    {
        _handler.Enqueue(HttpStatusCode.OK, "esto no es json");

        await Assert.ThrowsAsync<ScrapeException>(() => _service.FetchApiEventsAsync("2026-09-01", "2026-09-30"));
    }

    [Fact]
    public async Task FetchApiEventsAsync_WithHttpError_ThrowsScrapeException()
    {
        _handler.Enqueue(HttpStatusCode.InternalServerError, "error");

        var ex = await Assert.ThrowsAsync<ScrapeException>(() => _service.FetchApiEventsAsync("2026-09-01", "2026-09-30"));
        Assert.Contains("HTTP 500", ex.Message);
    }

    [Fact]
    public async Task ScrapeUpcomingAsync_MergesGridAndApiDeduplicatedByExternalId()
    {
        // /eventos carries the rich event 11; every API range carries 11 (basic) + 20.
        var apiJson = BuildApiJson(
            (11, "Jam de poesía", "2026-09-09T12:00:00.000Z", "https://www.instagram.com/p/X/", null),
            (20, "TANZ", "2026-09-25T20:00:00.000Z", null, null));

        _handler.Enqueue((_, _) => HtmlResponse(BuildGridHtml(
            ("11", "Jam de poesía", "2026-09-09T12:00:00.000Z", "Círculo Juan 23", "https://www.instagram.com/p/X/"))));
        for (var i = 0; i < 3; i++)
            _handler.Enqueue((_, _) => JsonResponse(apiJson));

        var events = await _service.ScrapeUpcomingAsync(2);

        // 1 listing + 3 month ranges (current + 2).
        Assert.Equal(4, _handler.Requests.Count);
        Assert.Equal(2, events.Count);

        var jam = events.Single(e => e.ExternalId == "11");
        Assert.Equal("Círculo Juan 23", jam.Venue); // la versión rica del listado gana
        Assert.Equal("Jam, Poesía", jam.Categories);

        var tanz = events.Single(e => e.ExternalId == "20");
        Assert.Equal("TANZ", tanz.Title);
        Assert.Null(tanz.Venue);
    }

    [Fact]
    public async Task ScrapeUpcomingAsync_ApiRangeFailure_SkipsMonthAndKeepsGrid()
    {
        _handler.Enqueue((_, _) => HtmlResponse(BuildGridHtml(
            ("11", "Jam de poesía", "2026-09-09T12:00:00.000Z", "Círculo Juan 23", null))));
        _handler.Enqueue(HttpStatusCode.InternalServerError, "error"); // un mes falla
        _handler.Enqueue((_, _) => JsonResponse(BuildApiJson(
            (20, "TANZ", "2026-09-25T20:00:00.000Z", null, null))));
        _handler.Enqueue((_, _) => JsonResponse(BuildApiJson(
            (20, "TANZ", "2026-09-25T20:00:00.000Z", null, null))));

        var events = await _service.ScrapeUpcomingAsync(2);

        Assert.Equal(4, _handler.Requests.Count); // se siguen pidiendo todos los meses
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.ExternalId == "11" && e.Venue == "Círculo Juan 23");
        Assert.Contains(events, e => e.ExternalId == "20");
    }
}
