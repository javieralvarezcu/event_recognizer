using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using EventRecognizer.Api.Models;

namespace EventRecognizer.Api.Services;

/// <summary>
/// Scrapes the muxojaleo.com calendar. The site's calendar page only server-renders a
/// subset of its events and loads the rest client-side from its own JSON API
/// (<c>/api/events?from=&amp;to=</c>), so the scraper combines two sources:
/// the <c>/eventos</c> listing (all published events with venue and categories already
/// resolved, serialized in an astro-island) and the <c>/api/events</c> range endpoint
/// (complete per range, used as a fallback for anything missing from the listing).
/// </summary>
public partial class MuxoScraperService : IMuxoScraperService
{
    private const string BaseUrl = "https://muxojaleo.com";

    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MuxoScraperService> _logger;

    public MuxoScraperService(IHttpClientFactory httpClientFactory, ILogger<MuxoScraperService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<MuxoEvent>> ScrapeUpcomingAsync(int monthsAhead, CancellationToken ct = default)
    {
        var byExternalId = new Dictionary<string, MuxoEvent>();

        // 1. The /eventos listing carries all published events with rich data
        //    (venue string and category names) inside an astro-island.
        var gridEvents = await FetchEventsGridAsync(ct);
        foreach (var evt in gridEvents)
            byExternalId.TryAdd(evt.ExternalId, evt);

        // 2. The calendar's own API is the client-side source for each month range:
        //    it is complete per range, so it fills anything the listing missed.
        var today = DateTime.UtcNow;
        for (var offset = 0; offset <= monthsAhead; offset++)
        {
            var month = today.AddMonths(offset);
            var (from, to) = GridWindow(month.Year, month.Month);

            List<MuxoEvent> apiEvents;
            try
            {
                apiEvents = await FetchApiEventsAsync(from, to, ct);
            }
            catch (ScrapeException ex)
            {
                // A single failing month range must not abort the whole scrape:
                // the listing above already covers the vast majority of events.
                _logger.LogWarning(ex, "Skipping muxojaleo API range {From} - {To}", from, to);
                continue;
            }

            foreach (var evt in apiEvents)
                byExternalId.TryAdd(evt.ExternalId, evt);
        }

        _logger.LogInformation("Scraped muxojaleo: {Count} events", byExternalId.Count);
        return byExternalId.Values.ToList();
    }

    /// <summary>Fetches and parses the /eventos listing page. Public for tests.</summary>
    public async Task<List<MuxoEvent>> FetchEventsGridAsync(CancellationToken ct = default)
        => ParseEventsGrid(await FetchPageAsync("/eventos", ct));

    /// <summary>Fetches and parses one /api/events range. Public for tests.</summary>
    public async Task<List<MuxoEvent>> FetchApiEventsAsync(string from, string to, CancellationToken ct = default)
    {
        var response = await FetchPageAsync($"/api/events?from={from}&to={to}", ct);
        List<MuxoApiEvent> apiEvents;
        try
        {
            apiEvents = JsonSerializer.Deserialize<List<MuxoApiEvent>>(response, ApiJsonOptions) ?? new List<MuxoApiEvent>();
        }
        catch (JsonException ex)
        {
            throw new ScrapeException("La API de eventos de muxojaleo.com no devolvió JSON válido.", ex);
        }

        return apiEvents
            .Where(e => e.Id > 0 && !string.IsNullOrWhiteSpace(e.Title))
            .Select(e => new MuxoEvent
            {
                ExternalId = e.Id.ToString(),
                Title = e.Title!,
                Date = e.Date != null && DateTime.TryParse(e.Date, out var parsed) ? parsed : null,
                Link = e.Link,
                Price = e.Price,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();
    }

    private async Task<string> FetchPageAsync(string pathAndQuery, CancellationToken ct)
    {
        var httpClient = _httpClientFactory.CreateClient("MuxoJaleo");
        HttpResponseMessage response;
        try
        {
            // Absolute URL so the service also works with clients that have no base address.
            response = await httpClient.GetAsync(BaseUrl + pathAndQuery, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ScrapeException("No se pudo conectar con muxojaleo.com.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ScrapeException(
                $"muxojaleo.com respondió con HTTP {(int)response.StatusCode} al pedir {pathAndQuery}.");
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Parses the /eventos page: the EventsGrid astro-island carries the published
    /// events with venue and categories already resolved (seroval format).
    /// </summary>
    public static List<MuxoEvent> ParseEventsGrid(string html)
    {
        var match = EventsGridIslandRegex().Match(html);
        if (!match.Success)
            throw new ScrapeException(
                "No se encontró el listado de eventos en muxojaleo.com/eventos (la estructura del sitio puede haber cambiado).");

        JsonDocument props;
        try
        {
            var decoded = WebUtility.HtmlDecode(match.Groups[1].Value);
            props = JsonDocument.Parse(decoded);
        }
        catch (JsonException ex)
        {
            throw new ScrapeException("Los datos del listado de muxojaleo.com no son JSON válido.", ex);
        }

        using (props)
        {
            if (!props.RootElement.TryGetProperty("initialEvents", out var eventsProp))
                throw new ScrapeException("El listado de muxojaleo.com no incluye los eventos.");

            var events = new List<MuxoEvent>();
            foreach (var item in UnwrapList(eventsProp))
            {
                var evt = ParseGridEvent(item);
                if (evt != null)
                    events.Add(evt);
            }

            return events;
        }
    }

    private static MuxoEvent? ParseGridEvent(JsonElement item)
    {
        var obj = UnwrapSeroval(item);
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        var externalId = GetString(obj, "id");
        var title = GetString(obj, "title");
        if (externalId == null || title == null)
            return null; // malformed entry: skip rather than fail the whole scrape

        return new MuxoEvent
        {
            ExternalId = externalId,
            Title = title,
            Date = GetDateTime(obj, "date"),
            Venue = GetString(obj, "venue"),
            Link = GetString(obj, "link"),
            Categories = GetCategories(obj),
            CreatedAt = DateTime.UtcNow
        };
    }

    private static string? GetCategories(JsonElement obj)
    {
        if (!obj.TryGetProperty("categories", out var raw))
            return null;

        var titles = UnwrapList(raw)
            .Select(UnwrapSeroval)
            .Where(c => c.ValueKind == JsonValueKind.Object)
            .Select(c => GetString(c, "title"))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!);
        var joined = string.Join(", ", titles);
        return joined.Length == 0 ? null : joined;
    }

    private static string? GetString(JsonElement obj, params string[] path)
    {
        var current = obj;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
                return null;
            current = UnwrapSeroval(current);
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            _ => null
        };
    }

    private static DateTime? GetDateTime(JsonElement obj, string key)
    {
        var value = GetString(obj, key);
        return value != null && DateTime.TryParse(value, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Seroval unwrapping: [0, x] wraps a scalar/object (or null), [1, [...]] is an
    /// array of wrapped items, [0] alone is null/undefined. Anything else is returned as-is.
    /// </summary>
    private static JsonElement UnwrapSeroval(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return value;

        var length = value.GetArrayLength();
        if (length == 1 && value[0].ValueKind == JsonValueKind.Number && value[0].GetInt32() == 0)
            return JsonDocument.Parse("null").RootElement;

        if (length == 2 && value[0].ValueKind == JsonValueKind.Number)
        {
            var tag = value[0].GetInt32();
            if (tag is 0 or 1)
                return value[1];
        }

        return value;
    }

    private static List<JsonElement> UnwrapList(JsonElement value)
    {
        var unwrapped = UnwrapSeroval(value);
        return unwrapped.ValueKind == JsonValueKind.Array
            ? unwrapped.EnumerateArray().ToList()
            : new List<JsonElement>();
    }

    /// <summary>
    /// Month range the site's calendar requests from its API: Monday before the first
    /// day of the month up to the Sunday after the last day (its grid window).
    /// </summary>
    private static (string From, string To) GridWindow(int year, int month)
    {
        var first = new DateTime(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var from = first.AddDays(-MondayBasedWeekday(first));
        var to = last.AddDays(6 - ((int)last.DayOfWeek + 6) % 7);
        return (from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
    }

    private static int MondayBasedWeekday(DateTime d)
        => d.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)d.DayOfWeek - 1;

    [GeneratedRegex("""<astro-island[^>]*component-url="/_astro/EventsGrid\.[^"]*"[^>]*props="([^"]+)""")]
    private static partial Regex EventsGridIslandRegex();

    /// <summary>Item shape of the /api/events JSON response.</summary>
    private sealed class MuxoApiEvent
    {
        public int Id { get; set; }

        public string? Title { get; set; }

        public string? Date { get; set; }

        public string? Link { get; set; }

        public string? Price { get; set; }
    }
}
