using System.Diagnostics;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using EventRecognizer.Api.Models;

namespace EventRecognizer.Api.Services;

public class DeepSeekService : IDeepSeekService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeepSeekService> _logger;
    private readonly IDeepSeekAuditService _auditService;
    private const string DeepSeekApiUrl = "https://api.deepseek.com/v1/chat/completions";
    private const string DeepSeekModel = "deepseek-chat";
    private const double DeepSeekTemperature = 0.3;

    // A batch of ~57 posts overflowed the 4096-token output limit and DeepSeek cut the
    // JSON mid-string ("Expected end of string, but instead reached end of data").
    // Process posts in small chunks so each response fits comfortably in the token budget.
    // The extraction chunk only holds posts the filter already marked as events. Chunks
    // of 30 overflowed again with real data (every attempt truncated at 4096 output
    // tokens, and truncated responses are still billed in full): 20 is the proven size.
    private const int PostsPerChunk = 20;

    // The phase-1 filter returns one boolean per post, so far larger chunks fit.
    private const int FilterPostsPerChunk = 60;
    private const int MaxAttempts = 3; // initial attempt + 2 retries

    // DeepSeek rate limits are tight: never send more than a couple of HTTP requests
    // at once, no matter how many runs are executing concurrently. Static so every
    // scoped instance of the service shares the same gate.
    private static readonly SemaphoreSlim RateLimitGate = new(2, 2);

    public DeepSeekService(
        IHttpClientFactory httpClientFactory,
        ILogger<DeepSeekService> logger,
        IDeepSeekAuditService auditService)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _auditService = auditService;
    }

    public async Task<List<PostAnalysisResult>> AnalyzePostsAsync(
        List<InstagramPost> posts,
        string apiKey,
        DateRange? dateRange = null,
        CancellationToken ct = default)
    {
        if (posts.Count == 0)
            return new List<PostAnalysisResult>();

        // Callers pair results with the input posts by index, so allocate the full
        // array and fill it in two phases, keeping the alignment intact.
        var results = new PostAnalysisResult[posts.Count];

        // Phase 1: cheap event/not-event filter over large chunks. The output is one
        // boolean per post, so many more posts fit per call and the prompt is minimal.
        // Non-events (~half of the posts) never pay the full extraction.
        var filterTotalChunks = (int)Math.Ceiling(posts.Count / (double)FilterPostsPerChunk);
        for (var offset = 0; offset < posts.Count; offset += FilterPostsPerChunk)
        {
            var chunk = posts.GetRange(offset, Math.Min(FilterPostsPerChunk, posts.Count - offset));
            var chunkNumber = offset / FilterPostsPerChunk + 1;

            var flags = await SendChatWithRetriesAsync(
                "analyze_posts_filter",
                $"chunk {chunkNumber}/{filterTotalChunks}: {chunk.Count} posts",
                BuildFilterSystemPrompt(), BuildFilterUserPrompt(chunk), apiKey, maxTokens: 2048,
                ParseBatchAnalysis, ct);
            if (flags == null)
            {
                // Exhausted all attempts: fail gracefully and treat the chunk as
                // non-events instead of failing the whole request.
                _logger.LogError(
                    "DeepSeek failed to filter {Count} posts after {MaxAttempts} attempts; returning them as non-events",
                    chunk.Count, MaxAttempts);
                flags = chunk.Select(_ => new PostAnalysisResult { IsEvent = false }).ToList();
            }

            // Pad with non-events if the LLM returned fewer results than requested
            // (and ignore extras), so index alignment is preserved no matter what.
            for (var i = 0; i < chunk.Count; i++)
            {
                results[offset + i] = i < flags.Count
                    ? flags[i]
                    : new PostAnalysisResult { IsEvent = false };
            }
        }

        // Phase 2: full extraction only for the posts the filter marked as events.
        // Chunks that keep overflowing the output token limit (truncated responses
        // are still billed in full, so re-sending the same chunk is money wasted)
        // are split in halves and retried instead.
        var eventIndexes = new List<int>();
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i].IsEvent)
                eventIndexes.Add(i);
        }

        var extractCall = 0;
        var pending = new Queue<List<int>>();
        for (var offset = 0; offset < eventIndexes.Count; offset += PostsPerChunk)
        {
            pending.Enqueue(eventIndexes.GetRange(offset, Math.Min(PostsPerChunk, eventIndexes.Count - offset)));
        }
        while (pending.Count > 0)
        {
            var indexChunk = pending.Dequeue();
            if (indexChunk.Count == 0)
                continue;

            var chunk = indexChunk.Select(i => posts[i]).ToList();
            extractCall++;
            var details = await SendChatWithRetriesAsync(
                "analyze_posts_extract",
                $"chunk {extractCall}: {chunk.Count} event posts",
                BuildSystemPrompt(), BuildUserPrompt(chunk, dateRange), apiKey, maxTokens: 4096,
                ParseBatchAnalysis, ct);
            if (details == null)
            {
                if (indexChunk.Count > 1)
                {
                    // Truncated again and again: split the chunk and retry the halves
                    // instead of burning more attempts on the same oversized input.
                    _logger.LogWarning(
                        "DeepSeek failed to extract {Count} event posts after {MaxAttempts} attempts; splitting the chunk",
                        chunk.Count, MaxAttempts);
                    var half = indexChunk.Count / 2;
                    pending.Enqueue(indexChunk.Take(half).ToList());
                    pending.Enqueue(indexChunk.Skip(half).ToList());
                    continue;
                }

                // A single post that still fails: give up and return it as non-event.
                _logger.LogError(
                    "DeepSeek failed to extract one event post after {MaxAttempts} attempts; returning it as non-event",
                    MaxAttempts);
                results[indexChunk[0]] = new PostAnalysisResult { IsEvent = false };
                continue;
            }

            // Pad with non-events if the LLM returned fewer results than requested
            // (and ignore extras), so index alignment is preserved no matter what.
            for (var i = 0; i < indexChunk.Count; i++)
            {
                results[indexChunk[i]] = i < details.Count
                    ? details[i]
                    : new PostAnalysisResult { IsEvent = false };
            }
        }

        return results.ToList();
    }

    private List<PostAnalysisResult> ParseBatchAnalysis(string content)
    {
        try
        {
            var result = JsonSerializer.Deserialize<BatchAnalysisResult>(content);
            return result?.Results ?? new List<PostAnalysisResult>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize DeepSeek response: {Content}",
                content[..Math.Min(500, content.Length)]);
            throw new InvalidOperationException("The LLM returned an unexpected response format.", ex);
        }
    }

    public async Task<List<DuplicateGroupResult>> FindDuplicateEventsAsync(
        List<CleanupEventItem> events,
        string monthLabel,
        string apiKey,
        CancellationToken ct = default)
    {
        if (events.Count == 0)
            return new List<DuplicateGroupResult>();

        var groups = await SendChatWithRetriesAsync(
            "cleanup_duplicates",
            $"{events.Count} events ({monthLabel})",
            BuildCleanupSystemPrompt(), BuildCleanupUserPrompt(events, monthLabel), apiKey, maxTokens: 8192,
            ParseDuplicateCleanup, ct);
        if (groups == null)
            throw new InvalidOperationException(
                $"The LLM failed to return a valid response for the month cleanup after {MaxAttempts} attempts.");

        return groups;
    }

    private List<DuplicateGroupResult> ParseDuplicateCleanup(string content)
    {
        try
        {
            var result = JsonSerializer.Deserialize<DuplicateCleanupResult>(content);
            return result?.DuplicateGroups ?? new List<DuplicateGroupResult>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize cleanup response: {Content}",
                content[..Math.Min(500, content.Length)]);
            throw new InvalidOperationException("The LLM returned an unexpected response format.", ex);
        }
    }

    public async Task<List<DuplicateGroupResult>> FindDuplicateCandidatesAsync(
        List<CleanupEventItem> candidates,
        List<CleanupEventItem> existingEvents,
        string apiKey,
        CancellationToken ct = default)
    {
        // Nothing to compare: no candidates, or a single candidate with no existing
        // events to compare it against (two or more candidates can still duplicate
        // each other).
        if (candidates.Count == 0 || (existingEvents.Count == 0 && candidates.Count < 2))
            return new List<DuplicateGroupResult>();

        var groups = await SendChatWithRetriesAsync(
            "dedup_candidates",
            $"{candidates.Count} new vs {existingEvents.Count} existing",
            BuildDedupCandidatesSystemPrompt(), BuildDedupCandidatesUserPrompt(candidates, existingEvents),
            apiKey, maxTokens: 8192, ParseDuplicateCleanup, ct);
        if (groups == null)
            throw new InvalidOperationException(
                $"The LLM failed to return a valid response for the duplicate check after {MaxAttempts} attempts.");

        return groups;
    }

    public async Task<List<CrossMatchResult>> FindCrossMatchesAsync(
        List<CleanupEventItem> ourEvents,
        List<MuxoEventItem> muxoEvents,
        string apiKey,
        CancellationToken ct = default)
    {
        if (ourEvents.Count == 0 || muxoEvents.Count == 0)
            return new List<CrossMatchResult>();

        var matches = await SendChatWithRetriesAsync(
            "cross_match",
            $"{ourEvents.Count} ours vs {muxoEvents.Count} muxojaleo",
            BuildCrossMatchSystemPrompt(), BuildCrossMatchUserPrompt(ourEvents, muxoEvents), apiKey, maxTokens: 8192,
            ParseCrossMatches, ct);
        if (matches == null)
            throw new InvalidOperationException(
                $"The LLM failed to return a valid response for the cross-match after {MaxAttempts} attempts.");

        return matches;
    }

    private List<CrossMatchResult> ParseCrossMatches(string content)
    {
        try
        {
            var result = JsonSerializer.Deserialize<CrossMatchBatchResult>(content);
            return result?.Matches ?? new List<CrossMatchResult>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize cross-match response: {Content}",
                content[..Math.Min(500, content.Length)]);
            throw new InvalidOperationException("The LLM returned an unexpected response format.", ex);
        }
    }

    private static readonly string[] SpanishDayNames =
        { "lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo" };

    private static string BuildCrossMatchSystemPrompt()
    {
        return """
            Eres un asistente que cruza dos listas de eventos culturales de Córdoba para determinar cuáles se refieren al MISMO evento real.

            Recibirás:
            1. NUESTROS EVENTOS: eventos detectados en posts de Instagram por un sistema de reconocimiento (título, resumen, fecha/recurrencia, cuenta, enlace del post y caption).
            2. EVENTOS DE MUXOJALEO: eventos del calendario de muxojaleo.com (título, fecha concreta, lugar, categorías y enlace).

            Tu tarea: devolver TODOS los pares (evento nuestro, evento de muxojaleo) que describan el mismo evento real. El título es solo UNA pista más: usa TODAS las señales disponibles (fecha, lugar, cuenta, enlace, resumen, caption y categorías).

            CÓMO DECIDIR (de más fuerte a más débil):
            - MISMO ENLACE: si el enlace de Instagram es idéntico en ambos, es el mismo evento (coincidencia SEGURA).
            - Si el enlace del evento de muxojaleo apunta a un PERFIL de Instagram (por ejemplo https://www.instagram.com/juevescong, sin /p/), compáralo con la CUENTA de nuestro evento: misma cuenta = mismo organizador, casi seguro el mismo evento si además la fecha/temática encaja.
            - MISMA FECHA + mismo lugar o cuenta (o temática claramente igual) = mismo evento aunque los títulos difieran.
            - NUESTRO evento recurrente semanal ("todos los jueves"): se corresponde con el evento de muxojaleo cuyo día de la semana coincida con el patrón (p. ej. un jueves) y cuyo lugar/cuenta sea el mismo. La fecha concreta del evento de muxojaleo debe caer dentro del patrón semanal.
            - Eventos de varios días nuestros ("diaria, del X al Y"): se corresponden con eventos de muxojaleo dentro de ese tramo con la misma temática/lugar.
            - Los títulos pueden variar entre fuentes: busca palabras distintivas compartidas ("El Camino", "L0rna", "Azabache", "Cosmopoética") o el nombre del lugar/sala mencionado en el resumen o caption de nuestro evento que coincida con el lugar del evento de muxojaleo.
            - Las categorías del evento de muxojaleo deben ser compatibles con la temática de nuestro evento (un concierto no se empareja con un taller de poesía).

            NO exijas títulos idénticos. Si la fecha y el lugar/cuenta coinciden y la temática es la misma, empareja. Solo evita emparejar cuando las señales se contradigan claramente.

            REGLAS DE FORMATO:
            - Cada evento puede aparecer como máximo en un par.
            - Solo puedes usar los IDs que aparecen en las listas. NUNCA inventes IDs.

            Responde ÚNICAMENTE con un objeto JSON con esta estructura:
            {
              "matches": [
                {
                  "event_unique_id": "ID de nuestro evento",
                  "muxo_event_id": "ID del evento de muxojaleo",
                  "reason": "Motivo breve en español"
                }
              ]
            }
            Si no hay coincidencias, devuelve { "matches": [] }.
            """;
    }

    private static string BuildCrossMatchUserPrompt(
        List<CleanupEventItem> ourEvents,
        List<MuxoEventItem> muxoEvents)
    {
        var sb = new StringBuilder();
        sb.AppendLine("NUESTROS EVENTOS:");

        foreach (var e in ourEvents)
        {
            sb.AppendLine($"--- EVENTO (ID: {e.EventUniqueId}) ---");
            sb.AppendLine($"título: {TruncateForPrompt(e.Title, 200)}");
            sb.AppendLine($"resumen: {TruncateForPrompt(e.Summary, 300)}");
            sb.AppendLine($"fecha: {(e.EventDate?.ToString("yyyy-MM-dd") ?? "(sin fecha concreta)")}");
            sb.AppendLine($"recurrencia: {FormatRecurrenceForPrompt(e)}");
            sb.AppendLine($"descripción de fecha: {TruncateForPrompt(e.EventDateDescription, 200)}");
            sb.AppendLine($"cuenta: {TruncateForPrompt(e.Account, 100)}");
            sb.AppendLine($"enlace del post: {e.Url ?? "(ninguno)"}");
            sb.AppendLine($"caption: {TruncateForPrompt(e.Caption, 400)}");
        }

        sb.AppendLine();
        sb.AppendLine("EVENTOS DE MUXOJALEO:");

        foreach (var m in muxoEvents)
        {
            sb.AppendLine($"--- EVENTO MUXO (ID: {m.ExternalId}) ---");
            sb.AppendLine($"título: {TruncateForPrompt(m.Title, 200)}");
            sb.AppendLine($"fecha: {(m.Date?.ToString("yyyy-MM-dd") ?? "(sin fecha)")}");
            sb.AppendLine($"lugar: {TruncateForPrompt(m.Venue, 150)}");
            sb.AppendLine($"categorías: {TruncateForPrompt(m.Categories, 150)}");
            sb.AppendLine($"enlace: {m.Link ?? "(ninguno)"}");
        }

        return sb.ToString();
    }

    /// <summary>Humanizes the recurrence pattern for the cross-match prompt.</summary>
    private static string FormatRecurrenceForPrompt(CleanupEventItem e)
    {
        if (!e.IsRecurrent)
            return "(ninguna)";

        var range = e.RecurrenceStartDate != null && e.RecurrenceEndDate != null
            ? $" (del {e.RecurrenceStartDate:yyyy-MM-dd} al {e.RecurrenceEndDate:yyyy-MM-dd})"
            : e.RecurrenceStartDate != null
                ? $" (desde {e.RecurrenceStartDate:yyyy-MM-dd}, sin fecha de fin)"
                : e.RecurrenceEndDate != null
                    ? $" (hasta {e.RecurrenceEndDate:yyyy-MM-dd})"
                    : string.Empty;

        if (e.RecurrenceType == "weekly")
        {
            var days = (e.RecurrenceDaysOfWeek ?? string.Empty)
                .Split(',')
                .Select(s => int.TryParse(s.Trim(), out var n) ? n : 0)
                .Where(n => n is >= 1 and <= 7)
                .Select(n => SpanishDayNames[n - 1])
                .ToList();
            return days.Count > 0
                ? $"semanal: {string.Join(", ", days)}{range}"
                : $"semanal{range}";
        }

        if (e.RecurrenceType == "daily")
            return $"diaria (evento de varios días){range}";

        return $"sí{range}";
    }

    /// <summary>
    /// Sends one chat request with retries and returns the parsed result, or null when
    /// all attempts produced malformed responses. Transient HTTP errors rethrow after
    /// the last attempt so callers can return 502 instead of fake results. Every
    /// attempt (request, response, tokens and outcome) is recorded in the audit log.
    /// </summary>
    private async Task<T?> SendChatWithRetriesAsync<T>(
        string operation,
        string contextSummary,
        string systemPrompt,
        string userPrompt,
        string apiKey,
        int maxTokens,
        Func<string, T> parse,
        CancellationToken ct)
    {
        var attempt = 1;
        while (true)
        {
            var log = NewExchangeLog(operation, contextSummary, systemPrompt, userPrompt, maxTokens, attempt);
            var stopwatch = Stopwatch.StartNew();
            var retry = false;
            var rethrow = false;
            Exception? failure = null;

            try
            {
                var http = await SendChatOnceAsync(systemPrompt, userPrompt, apiKey, maxTokens, ct);
                ApplyHttpResult(log, http);

                try
                {
                    var parsed = parse(http.Content);
                    log.Succeeded = true;
                    FinishExchangeLog(log, stopwatch);
                    await _auditService.RecordAsync(log, ct);
                    return parsed;
                }
                catch (InvalidOperationException ex)
                {
                    // The HTTP exchange was fine but the LLM content is not usable
                    // (bad JSON): worth retrying — the model often produces a valid
                    // response on a second attempt.
                    failure = ex;
                    log.ErrorMessage = $"Failed to parse the LLM content: {ex.Message}";
                    retry = true;
                }
            }
            catch (DeepSeekResponseException ex)
            {
                // Truncated/malformed envelope from the LLM: worth retrying too.
                failure = ex;
                log.HttpStatusCode = (int)ex.StatusCode;
                log.ResponseContent = ex.RawBody;
                log.ErrorMessage = ex.Message;
                if (ex.RawBody != null)
                    log.CompletionTokens = EstimateTokens(ex.RawBody.Length);
                retry = true;
            }
            catch (HttpRequestException ex)
            {
                failure = ex;
                log.HttpStatusCode = (int?)ex.StatusCode;
                log.ErrorMessage = ex.Message;
                retry = IsTransientHttpError(ex);
                rethrow = !retry; // non-transient (401, 404...): propagate immediately
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // HttpClient timeout: no HTTP response ever arrived.
                failure = ex;
                log.ErrorMessage = "The DeepSeek API call timed out.";
                rethrow = true;
            }
            catch (OperationCanceledException)
            {
                throw; // the client cancelled the request: propagate, nothing to audit
            }

            FinishExchangeLog(log, stopwatch);
            await _auditService.RecordAsync(log, ct);

            if (rethrow)
            {
                _logger.LogError(failure, "DeepSeek API call failed ({Operation})", operation);
                ExceptionDispatchInfo.Capture(failure!).Throw(); // preserves the original stack trace
            }

            if (attempt >= MaxAttempts)
            {
                if (failure is HttpRequestException)
                {
                    // API unreachable: let the controller return 502 rather than fake results.
                    _logger.LogError(failure,
                        "DeepSeek API returned a transient HTTP error after {MaxAttempts} attempts",
                        MaxAttempts);
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }

                _logger.LogError(failure,
                    "DeepSeek returned an unexpected response format after {MaxAttempts} attempts",
                    MaxAttempts);
                return default;
            }

            _logger.LogWarning(failure,
                "DeepSeek call failed (attempt {Attempt}/{MaxAttempts}). Retrying...",
                attempt, MaxAttempts);

            await DelayBetweenRetriesAsync(attempt, ct);
            attempt++;
        }
    }

    /// <summary>Creates the audit row for one attempt with everything known before sending.</summary>
    private static DeepSeekCallLog NewExchangeLog(
        string operation,
        string contextSummary,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        int attempt)
    {
        return new DeepSeekCallLog
        {
            Operation = operation,
            ContextSummary = contextSummary,
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Model = DeepSeekModel,
            MaxTokens = maxTokens,
            Temperature = DeepSeekTemperature,
            PromptTokens = EstimateTokens(systemPrompt.Length + userPrompt.Length),
            TokensEstimated = true, // switched off by ApplyHttpResult when the API reports usage
            Attempt = attempt,
            StartedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>Fills the audit row with the HTTP response data.</summary>
    private static void ApplyHttpResult(DeepSeekCallLog log, DeepSeekHttpResult http)
    {
        log.HttpStatusCode = (int)HttpStatusCode.OK;
        log.FinishReason = http.FinishReason;
        log.ResponseContent = http.Content;

        if (http.Usage != null)
        {
            log.PromptTokens = http.Usage.PromptTokens;
            log.CompletionTokens = http.Usage.CompletionTokens;
            log.TokensEstimated = false;
        }
        else
        {
            log.CompletionTokens = EstimateTokens(http.Content.Length);
        }
    }

    /// <summary>Stamps the end time, duration and total tokens of the exchange.</summary>
    private static void FinishExchangeLog(DeepSeekCallLog log, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        log.CompletedAtUtc = DateTime.UtcNow;
        log.DurationMs = stopwatch.ElapsedMilliseconds;
        log.TotalTokens = log.PromptTokens + log.CompletionTokens;
    }

    /// <summary>Heuristic token estimate used when the API does not report usage: ~4 chars per token.</summary>
    private static int EstimateTokens(int charCount) => charCount / 4;

    // Retry backoff between attempts (2s, then 4s — enough for 429 rate limits to
    // recover without hammering). Virtual so tests can skip the delay.
    protected virtual Task DelayBetweenRetriesAsync(int attempt, CancellationToken ct)
        => Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);

    private static bool IsTransientHttpError(HttpRequestException ex) =>
        ex.StatusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable;

    private async Task<DeepSeekHttpResult> SendChatOnceAsync(
        string systemPrompt,
        string userPrompt,
        string apiKey,
        int maxTokens,
        CancellationToken ct)
    {
        var requestPayload = new DeepSeekRequest
        {
            Model = DeepSeekModel,
            Messages = new List<DeepSeekMessage>
            {
                new() { Role = "system", Content = systemPrompt },
                new() { Role = "user", Content = userPrompt }
            },
            ResponseFormat = new DeepSeekResponseFormat { Type = "json_object" },
            Temperature = DeepSeekTemperature,
            MaxTokens = maxTokens
        };

        var json = JsonSerializer.Serialize(requestPayload);

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, DeepSeekApiUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        requestMessage.Headers.Add("Authorization", $"Bearer {apiKey}");

        var httpClient = _httpClientFactory.CreateClient("DeepSeek");
        await RateLimitGate.WaitAsync(ct);
        try
        {
            var response = await httpClient.SendAsync(requestMessage, ct);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            return ParseDeepSeekResponse(responseBody);
        }
        finally
        {
            RateLimitGate.Release();
        }
    }

    private DeepSeekHttpResult ParseDeepSeekResponse(string responseBody)
    {
        DeepSeekResponse? deepSeekResponse;
        try
        {
            deepSeekResponse = JsonSerializer.Deserialize<DeepSeekResponse>(responseBody);
        }
        catch (JsonException ex)
        {
            throw new DeepSeekResponseException(
                "The DeepSeek API returned a malformed response.", responseBody, HttpStatusCode.OK, ex);
        }

        if (deepSeekResponse?.Choices == null || deepSeekResponse.Choices.Count == 0)
            throw new DeepSeekResponseException("DeepSeek returned no choices.", responseBody, HttpStatusCode.OK);

        var choice = deepSeekResponse.Choices[0];

        // finish_reason == "length" means the output hit the token limit, so the JSON
        // is almost certainly truncated — treat it as a malformed response and retry.
        if (string.Equals(choice.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
            throw new DeepSeekResponseException(
                "DeepSeek response was truncated by the token limit (finish_reason=length).",
                responseBody, HttpStatusCode.OK);

        var responseContent = choice.Message?.Content;
        if (string.IsNullOrWhiteSpace(responseContent))
            throw new DeepSeekResponseException("DeepSeek response content was empty.", responseBody, HttpStatusCode.OK);

        _logger.LogInformation("Raw DeepSeek response received ({Length} chars)", responseContent.Length);

        return new DeepSeekHttpResult(responseContent, choice.FinishReason, deepSeekResponse.Usage);
    }

    /// <summary>One successful HTTP exchange with DeepSeek, with the data the audit log keeps.</summary>
    private sealed record DeepSeekHttpResult(string Content, string? FinishReason, DeepSeekUsage? Usage);

    /// <summary>
    /// The DeepSeek HTTP exchange completed but the response envelope is unusable
    /// (malformed JSON, no choices, truncated, empty content). Carries the raw body
    /// so the audit log keeps what the API actually returned.
    /// </summary>
    private sealed class DeepSeekResponseException : InvalidOperationException
    {
        public string? RawBody { get; }
        public HttpStatusCode StatusCode { get; }

        public DeepSeekResponseException(
            string message, string? rawBody, HttpStatusCode statusCode, Exception? inner = null)
            : base(message, inner)
        {
            RawBody = rawBody;
            StatusCode = statusCode;
        }
    }

    /// <summary>
    /// Extraction prompt (phase 2): the posts it receives were already classified
    /// as events by the cheap filter, so it only needs to extract their fields.
    /// Kept lean — no event-detection rules, two examples, compact schema.
    /// </summary>
    private static string BuildSystemPrompt()
    {
        return """
            Eres un asistente especializado en extraer la información de un cartel de evento de Instagram (concierto, fiesta, festival, club night, feria, exposición, obra de teatro, etc.).

            Para cada publicación debes determinar:
            0. **is_event** (bool): true (todos los posts que recibes son eventos).
            1. **title** (string | null): título del evento.
            2. **event_date** (string | null): fecha y hora en formato ISO 8601 (YYYY-MM-DDTHH:mm:ss). null si no se puede determinar una fecha concreta.
            3. **event_date_description** (string | null): descripción textual de la fecha ("Todos los jueves", "Sábado 19 de septiembre de 2026", "Del 14 al 17 de septiembre"). null si no aplica.
            4. **summary** (string | null): resumen MUY breve en español (máx. 20 palabras) describiendo el evento.
            5. **is_recurrent** (bool): true si el evento se repite en el tiempo (patrón semanal o varios días seguidos).
            6. **recurrence_type** (string | null): "weekly" (se repite por días de la semana) o "daily" (cada día dentro de un rango de fechas). null si no es recurrente.
            7. **recurrence_days_of_week** (array | null): días de repetición, 1=lunes...7=domingo. "De lunes a jueves" → [1,2,3,4]. "Todos los jueves" → [4].
            8. **recurrence_start_date** (string | null, YYYY-MM-DD): primer día de la recurrencia o del rango.
            9. **recurrence_end_date** (string | null, YYYY-MM-DD): último día de la recurrencia o del rango. null si es indefinida.

            REGLAS DE FECHAS (OBLIGATORIO):
            - Si el cartel menciona un evento con nombre propio conocido ("Feria de Córdoba", "San Isidro", "WOMAD", "Sónar"...), DEBES buscar en tu conocimiento las fechas reales de la edición anunciada y rellenarlas. Está PROHIBIDO dejar las fechas a null cuando conoces el evento: SÍ O SÍ debes resolverlas, aunque el cartel no las diga explícitamente.
            - Si el cartel usa fechas relativas ("este sábado", "el próximo viernes"), resuélvelas a fechas concretas usando la fecha de publicación del post y el rango de fechas solicitado si aparece en el mensaje.
            - Si el cartel indica un rango de días ("del 14 al 17 de septiembre"), refleja el rango completo en recurrence_start_date / recurrence_end_date con recurrence_type "daily".
            - Un evento recurrente sin fechas ("todos los jueves" a secas): recurrence_type "weekly" con los días correspondientes. Si no puedes determinar ninguna fecha en absoluto, deja is_recurrent true con todas las fechas a null (el sistema lo tratará como no verificable).
            - Si además de un patrón recurrente conoces una próxima fecha concreta, ponla también en event_date.

            EJEMPLO 1 — "FERIA DE CÓRDOBA 2026 · Del 23 al 30 de mayo · Caseta Municipal":
            {
              "is_event": true,
              "title": "Feria de Córdoba 2026",
              "event_date": "2026-05-23",
              "event_date_description": "Del 23 al 30 de mayo de 2026",
              "summary": "Feria de Córdoba con casetas, música y actividades en la Caseta Municipal.",
              "is_recurrent": true,
              "recurrence_type": "daily",
              "recurrence_days_of_week": null,
              "recurrence_start_date": "2026-05-23",
              "recurrence_end_date": "2026-05-30"
            }

            EJEMPLO 2 — "TECHNO THURSDAYS · Todos los jueves desde el 10 de septiembre":
            {
              "is_event": true,
              "title": "Techno Thursdays",
              "event_date": "2026-09-10T23:00:00",
              "event_date_description": "Todos los jueves desde el 10 de septiembre de 2026",
              "summary": "Noche de techno todos los jueves.",
              "is_recurrent": true,
              "recurrence_type": "weekly",
              "recurrence_days_of_week": [4],
              "recurrence_start_date": "2026-09-10",
              "recurrence_end_date": null
            }

            Responde ÚNICAMENTE con un objeto JSON: { "results": [ { ...campos 0-9 por cada publicación, en el mismo orden... } ] }
            """;
    }

    /// <summary>
    /// Filter prompt (phase 1): only the event/not-event decision, no extraction,
    /// no examples. Tiny input and tiny output, so large chunks are cheap.
    /// </summary>
    private static string BuildFilterSystemPrompt()
    {
        return """
            Eres un asistente que clasifica publicaciones de Instagram. Para cada publicación determina únicamente si es el cartel/anuncio de un evento (concierto, fiesta, festival, club night, feria, exposición, obra de teatro...) o no lo es.

            Un post SÍ es cartel de evento cuando anuncia un evento específico futuro, con fecha concreta o recurrencia clara ("todos los jueves", "del 14 al 17").
            NO son eventos:
            - Agradecimientos, resúmenes o recaps de eventos pasados.
            - Anuncios de merchandising, pre-orders o sorteos aislados.
            - Posts tipo "soon"/"próximamente" sin detalles ni fecha.
            - Memes, contenido entre bastidores o fotos sueltas sin anuncio.

            Responde ÚNICAMENTE con un objeto JSON (un elemento del array "results" por cada publicación, en el mismo orden):
            { "results": [ { "is_event": true/false } ] }
            """;
    }

    private static string BuildUserPrompt(List<InstagramPost> posts, DateRange? dateRange)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Analiza las siguientes publicaciones de Instagram (una por cada elemento del array):");

        if (dateRange != null)
        {
            sb.AppendLine();
            sb.AppendLine("RANGO DE FECHAS SOLICITADO POR EL USUARIO:");
            sb.AppendLine($"- Desde: {(dateRange.From?.ToString("yyyy-MM-dd") ?? "(sin límite)")}");
            sb.AppendLine($"- Hasta: {(dateRange.To?.ToString("yyyy-MM-dd") ?? "(sin límite)")}");
            sb.AppendLine("Utiliza este rango como referencia para resolver fechas relativas o aproximadas.");
            sb.AppendLine("IMPORTANTE: NO filtres ni descartes eventos por este rango; reporta toda la información de fechas y recurrencia de cada publicación. El sistema aplicará el filtro.");
        }

        sb.AppendLine();

        for (int i = 0; i < posts.Count; i++)
        {
            var post = posts[i];
            sb.AppendLine($"--- POST {i} ---");
            sb.AppendLine($"account: {post.Account}");
            sb.AppendLine($"caption: {TruncateForPrompt(post.Caption, 800)}");
            sb.AppendLine($"datetime: {(post.Datetime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "null")}");
            sb.AppendLine($"url: {post.Url}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Phase-1 user prompt: same posts, but shorter captions and no date range
    /// (the range only matters for date resolution, not for the event decision).
    /// </summary>
    private static string BuildFilterUserPrompt(List<InstagramPost> posts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Clasifica las siguientes publicaciones de Instagram:");

        for (int i = 0; i < posts.Count; i++)
        {
            var post = posts[i];
            sb.AppendLine($"--- POST {i} ---");
            sb.AppendLine($"account: {post.Account}");
            sb.AppendLine($"caption: {TruncateForPrompt(post.Caption, 600)}");
            sb.AppendLine($"datetime: {(post.Datetime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "null")}");
            sb.AppendLine($"url: {post.Url}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildCleanupSystemPrompt()
    {
        return """
            Eres un asistente especializado en detectar eventos duplicados en un registro de eventos extraídos de publicaciones de Instagram.

            Recibirás una lista de eventos persistidos: los del mes indicado (con fecha concreta o recurrencia que cae en ese mes) y, al final, los eventos sin fecha. Varios de estos eventos pueden ser en realidad EL MISMO evento real anunciado en posts distintos (por ejemplo, la misma fiesta semanal anunciada cada semana, o el mismo concierto publicado por la sala y por el artista).

            Tu tarea: agrupar los eventos que sean el mismo evento real y decidir, por cada grupo, cuál se conserva y cuáles se eliminan por duplicados.

            REGLAS DE COMPARACIÓN (MUY IMPORTANTE):
            - NO te fíes solo del título: los títulos los genera un LLM y pueden variar entre posts ("Fiesta jueves", "Jueves de fiesta", "Thursday party"). Compara el conjunto: cuenta/lugar, resumen, fecha o patrón de recurrencia, y texto del caption.
            - Dos eventos son el mismo cuando coinciden el lugar/cuenta Y la fecha (o el mismo patrón de recurrencia, p. ej. ambos "todos los jueves" en la misma cuenta) aunque los títulos difieran.
            - Si un evento SIN FECHA coincide con un evento CON FECHA del mes (misma cuenta y misma temática/recurrencia), conserva SIEMPRE el que tiene fecha y marca el sin fecha como duplicado.
            - Si dos eventos sin fecha son el mismo, conserva el más completo (más resumen, más información) y marca el otro.
            - Si tienes dudas razonables de que sean el mismo evento, NO los agrupes. Ante la duda, conserva todos.
            - Solo puedes usar los IDs que aparecen en la lista. NUNCA inventes IDs.
            - Un evento debe aparecer como máximo en un grupo (no lo marques como duplicado en varios grupos a la vez).

            Responde ÚNICAMENTE con un objeto JSON con esta estructura:
            {
              "duplicate_groups": [
                {
                  "keep_event_id": "ID del evento que se conserva",
                  "duplicate_event_ids": ["ID del evento duplicado a eliminar", "..."],
                  "reason": "Motivo breve en español"
                }
              ]
            }
            Si no hay duplicados, devuelve { "duplicate_groups": [] }.
            """;
    }

    private static string BuildCleanupUserPrompt(List<CleanupEventItem> events, string monthLabel)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Mes que se está limpiando: {monthLabel} (los eventos con fecha de este mes y los eventos sin fecha).");

        foreach (var e in events)
            AppendEventItem(sb, e, "EVENTO", noDateLabel: "SIN FECHA");

        return sb.ToString();
    }

    /// <summary>
    /// Detects which newly recognized events are the same real event as one already
    /// persisted (or as another new event of the same batch), so the duplicates are
    /// not saved again. Same response contract as the cleanup duplicate detection.
    /// </summary>
    private static string BuildDedupCandidatesSystemPrompt()
    {
        return """
            Eres un asistente especializado en detectar si un evento recién reconocido ya existe en el registro de eventos guardados.

            Recibirás dos listas:
            1. EVENTOS NUEVOS: eventos recién reconocidos en posts de Instagram que AÚN NO están guardados.
            2. EVENTOS EXISTENTES: eventos ya guardados en la base de datos.

            Tu tarea: identificar qué eventos NUEVOS son en realidad EL MISMO evento real que uno YA EXISTENTE (por ejemplo, la misma fiesta semanal anunciada cada semana desde la misma cuenta, o el mismo concierto publicado por la sala y por el artista). Esos eventos nuevos NO se guardarán: se conserva el evento existente.

            REGLAS DE COMPARACIÓN (MUY IMPORTANTE):
            - NO te fíes solo del título: los títulos los genera un LLM y pueden variar entre posts ("Fiesta jueves", "Jueves de fiesta", "Thursday party"). Compara el conjunto: cuenta/lugar, resumen, fecha o patrón de recurrencia, y texto del caption.
            - Dos eventos son el mismo cuando coinciden el lugar/cuenta Y la fecha (o el mismo patrón de recurrencia, p. ej. ambos "todos los jueves" en la misma cuenta) aunque los títulos difieran.
            - Si tienes dudas razonables de que sean el mismo evento, NO los agrupes. Ante la duda, conserva el evento nuevo.
            - Un evento EXISTENTE nunca se marca como duplicado: si un grupo mezcla eventos nuevos y existentes, keep_event_id SIEMPRE es el ID del evento existente y duplicate_event_ids los eventos nuevos.
            - Si dos eventos NUEVOS son duplicados entre sí y ninguno existe todavía, agrupa igualmente: keep_event_id será el evento nuevo más completo y duplicate_event_ids los demás.
            - Solo puedes usar los IDs que aparecen en las listas. NUNCA inventes IDs.
            - Un evento debe aparecer como máximo en un grupo.

            Responde ÚNICAMENTE con un objeto JSON con esta estructura:
            {
              "duplicate_groups": [
                {
                  "keep_event_id": "ID del evento que se conserva",
                  "duplicate_event_ids": ["ID del evento duplicado que no se guardará", "..."],
                  "reason": "Motivo breve en español"
                }
              ]
            }
            Si no hay duplicados, devuelve { "duplicate_groups": [] }.
            """;
    }

    private static string BuildDedupCandidatesUserPrompt(
        List<CleanupEventItem> candidates,
        List<CleanupEventItem> existingEvents)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Compara los eventos nuevos con los existentes y devuelve los grupos de duplicados.");
        sb.AppendLine();
        sb.AppendLine("EVENTOS NUEVOS (recién reconocidos, aún no guardados):");

        foreach (var e in candidates)
            AppendEventItem(sb, e, "EVENTO NUEVO", noDateLabel: "sin fecha");

        sb.AppendLine();
        sb.AppendLine("EVENTOS EXISTENTES (ya guardados):");

        foreach (var e in existingEvents)
            AppendEventItem(sb, e, "EVENTO EXISTENTE", noDateLabel: "sin fecha");

        return sb.ToString();
    }

    /// <summary>Appends one event's fields to a duplicate-detection prompt.</summary>
    private static void AppendEventItem(StringBuilder sb, CleanupEventItem e, string label, string noDateLabel)
    {
        sb.AppendLine();
        sb.AppendLine($"--- {label} (ID: {e.EventUniqueId}) ---");
        sb.AppendLine($"título: {TruncateForPrompt(e.Title, 200)}");
        sb.AppendLine($"resumen: {TruncateForPrompt(e.Summary, 300)}");
        sb.AppendLine($"fecha: {(e.EventDate?.ToString("yyyy-MM-dd") ?? $"({noDateLabel})")}");
        sb.AppendLine($"descripción de fecha: {TruncateForPrompt(e.EventDateDescription, 200)}");
        sb.AppendLine($"recurrente: {e.IsRecurrent}");
        sb.AppendLine($"tipo de recurrencia: {e.RecurrenceType ?? "(ninguno)"}");
        sb.AppendLine($"días de la semana: {(string.IsNullOrWhiteSpace(e.RecurrenceDaysOfWeek) ? "(ninguno)" : e.RecurrenceDaysOfWeek)}");
        sb.AppendLine($"inicio recurrencia: {e.RecurrenceStartDate?.ToString("yyyy-MM-dd") ?? "(ninguno)"}");
        sb.AppendLine($"fin recurrencia: {e.RecurrenceEndDate?.ToString("yyyy-MM-dd") ?? "(ninguno)"}");
        sb.AppendLine($"cuenta: {TruncateForPrompt(e.Account, 100)}");
        sb.AppendLine($"enlace: {e.Url ?? "(ninguno)"}");
        sb.AppendLine($"caption: {TruncateForPrompt(e.Caption, 500)}");
    }

    private static string TruncateForPrompt(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return "(vacío)";
        return text.Length <= maxChars ? text : text[..maxChars] + "...";
    }
}
