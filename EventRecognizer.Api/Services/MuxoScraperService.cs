using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using EventRecognizer.Api.Models;

namespace EventRecognizer.Api.Services;

/// <summary>
/// Scrapes the muxojaleo.com calendar. The page is a server-rendered Astro app:
/// the calendar's events travel serialized inside an &lt;astro-island&gt; tag's
/// "props" attribute (seroval format), so no headless browser is needed.
/// </summary>
public partial class MuxoScraperService : IMuxoScraperService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MuxoScraperService> _logger;

    public MuxoScraperService(IHttpClientFactory httpClientFactory, ILogger<MuxoScraperService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<MuxoEvent>> ScrapeUpcomingAsync(int monthsAhead, CancellationToken ct = default)
    {
        var today = DateTime.UtcNow;
        var months = Enumerable.Range(0, monthsAhead + 1)
            .Select(offset => today.AddMonths(offset).ToString("yyyy-MM"))
            .ToList();

        var byExternalId = new Dictionary<string, MuxoEvent>();
        foreach (var month in months)
        {
            foreach (var evt in await ScrapeMonthAsync(month, ct))
            {
                // An event spanning several days appears on every month page it touches:
                // keep the first occurrence and ignore the rest.
                byExternalId.TryAdd(evt.ExternalId, evt);
            }
        }

        _logger.LogInformation(
            "Scraped {Months} months of the muxojaleo calendar: {Count} events",
            months.Count, byExternalId.Count);
        return byExternalId.Values.ToList();
    }

    /// <summary>Scrapes a single month page (e.g. "2026-09"). Public for tests.</summary>
    public async Task<List<MuxoEvent>> ScrapeMonthAsync(string yearMonth, CancellationToken ct = default)
    {
        var httpClient = _httpClientFactory.CreateClient("MuxoJaleo");
        HttpResponseMessage response;
        try
        {
            // Absolute URL so the service also works with clients that have no base address.
            response = await httpClient.GetAsync($"https://muxojaleo.com/calendario?month={yearMonth}", ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ScrapeException("No se pudo conectar con muxojaleo.com.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ScrapeException(
                $"muxojaleo.com respondió con HTTP {(int)response.StatusCode} al pedir el mes {yearMonth}.");
        }

        var html = await response.Content.ReadAsStringAsync(ct);
        return ParseEvents(html);
    }

    public static List<MuxoEvent> ParseEvents(string html)
    {
        var match = CalendarIslandRegex().Match(html);
        if (!match.Success)
            throw new ScrapeException(
                "No se encontró el calendario en la página de muxojaleo.com (la estructura del sitio puede haber cambiado).");

        JsonDocument props;
        try
        {
            var decoded = WebUtility.HtmlDecode(match.Groups[1].Value);
            props = JsonDocument.Parse(decoded);
        }
        catch (JsonException ex)
        {
            throw new ScrapeException("Los datos del calendario de muxojaleo.com no son JSON válido.", ex);
        }

        using (props)
        {
            if (!props.RootElement.TryGetProperty("events", out var eventsProp))
                throw new ScrapeException("El calendario de muxojaleo.com no incluye la lista de eventos.");

            var events = new List<MuxoEvent>();
            foreach (var item in UnwrapList(eventsProp))
            {
                var evt = ParseEvent(item);
                if (evt != null)
                    events.Add(evt);
            }

            return events;
        }
    }

    private static MuxoEvent? ParseEvent(JsonElement item)
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
            Venue = GetString(obj, "location", "venue"),
            Link = GetString(obj, "link"),
            Categories = GetCategories(obj),
            Price = GetString(obj, "price"),
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

    [GeneratedRegex("""<astro-island[^>]*component-url="/_astro/Calendar\.[^"]*"[^>]*props="([^"]+)""")]
    private static partial Regex CalendarIslandRegex();
}
