using System.Net;
using System.Text;
using System.Text.Json;
using EventRecognizer.Api.Models;

namespace EventRecognizer.Api.Services;

public class DeepSeekService : IDeepSeekService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeepSeekService> _logger;
    private const string DeepSeekApiUrl = "https://api.deepseek.com/v1/chat/completions";

    // A batch of ~57 posts overflowed the 4096-token output limit and DeepSeek cut the
    // JSON mid-string ("Expected end of string, but instead reached end of data").
    // Process posts in small chunks so each response fits comfortably in the token budget.
    private const int PostsPerChunk = 20;
    private const int MaxAttempts = 3; // initial attempt + 2 retries

    public DeepSeekService(IHttpClientFactory httpClientFactory, ILogger<DeepSeekService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
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
        // array and fill it chunk by chunk, keeping the alignment intact.
        var results = new PostAnalysisResult[posts.Count];

        for (var offset = 0; offset < posts.Count; offset += PostsPerChunk)
        {
            var chunk = posts.GetRange(offset, Math.Min(PostsPerChunk, posts.Count - offset));

            var chunkResults = await AnalyzeChunkWithRetriesAsync(chunk, apiKey, dateRange, ct);
            if (chunkResults == null)
            {
                // Exhausted all attempts: fail gracefully and treat the chunk as
                // non-events instead of failing the whole request.
                _logger.LogError(
                    "DeepSeek failed to analyze {Count} posts after {MaxAttempts} attempts; returning them as non-events",
                    chunk.Count, MaxAttempts);
                chunkResults = chunk.Select(_ => new PostAnalysisResult { IsEvent = false }).ToList();
            }

            // Pad with non-events if the LLM returned fewer results than requested
            // (and ignore extras), so index alignment is preserved no matter what.
            for (var i = 0; i < chunk.Count; i++)
            {
                results[offset + i] = i < chunkResults.Count
                    ? chunkResults[i]
                    : new PostAnalysisResult { IsEvent = false };
            }
        }

        return results.ToList();
    }

    private async Task<List<PostAnalysisResult>?> AnalyzeChunkWithRetriesAsync(
        List<InstagramPost> chunk,
        string apiKey,
        DateRange? dateRange,
        CancellationToken ct)
    {
        var attempt = 1;
        while (true)
        {
            try
            {
                return await AnalyzeChunkAsync(chunk, apiKey, dateRange, ct);
            }
            catch (InvalidOperationException ex)
            {
                if (attempt >= MaxAttempts)
                {
                    _logger.LogError(ex,
                        "DeepSeek returned an unexpected response format after {MaxAttempts} attempts",
                        MaxAttempts);
                    return null;
                }

                // Truncated/malformed JSON from the LLM: worth retrying — the model
                // often produces a valid response on a second attempt.
                _logger.LogWarning(ex,
                    "DeepSeek returned an unexpected response format (attempt {Attempt}/{MaxAttempts}). Retrying...",
                    attempt, MaxAttempts);
            }
            catch (HttpRequestException ex) when (IsTransientHttpError(ex))
            {
                if (attempt >= MaxAttempts)
                {
                    _logger.LogError(ex,
                        "DeepSeek API returned a transient HTTP error after {MaxAttempts} attempts",
                        MaxAttempts);
                    throw; // API unreachable: let the controller return 502 rather than fake results
                }

                _logger.LogWarning(ex,
                    "DeepSeek API returned a transient HTTP error (attempt {Attempt}/{MaxAttempts}). Retrying...",
                    attempt, MaxAttempts);
            }

            await DelayBetweenRetriesAsync(attempt, ct);
            attempt++;
        }
    }

    // Retry backoff between attempts. Virtual so tests can skip the delay.
    protected virtual Task DelayBetweenRetriesAsync(int attempt, CancellationToken ct)
        => Task.Delay(TimeSpan.FromSeconds(attempt), ct);

    private static bool IsTransientHttpError(HttpRequestException ex) =>
        ex.StatusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable;

    private async Task<List<PostAnalysisResult>> AnalyzeChunkAsync(
        List<InstagramPost> posts,
        string apiKey,
        DateRange? dateRange,
        CancellationToken ct)
    {
        var systemPrompt = BuildSystemPrompt();
        var userPrompt = BuildUserPrompt(posts, dateRange);

        var requestPayload = new DeepSeekRequest
        {
            Model = "deepseek-chat",
            Messages = new List<DeepSeekMessage>
            {
                new() { Role = "system", Content = systemPrompt },
                new() { Role = "user", Content = userPrompt }
            },
            ResponseFormat = new DeepSeekResponseFormat { Type = "json_object" },
            Temperature = 0.3,
            MaxTokens = 4096
        };

        var json = JsonSerializer.Serialize(requestPayload);

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, DeepSeekApiUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        requestMessage.Headers.Add("Authorization", $"Bearer {apiKey}");

        _logger.LogInformation("Sending {Count} posts to DeepSeek for analysis", posts.Count);

        var httpClient = _httpClientFactory.CreateClient("DeepSeek");
        var response = await httpClient.SendAsync(requestMessage, ct);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(ct);

        DeepSeekResponse? deepSeekResponse;
        try
        {
            deepSeekResponse = JsonSerializer.Deserialize<DeepSeekResponse>(responseBody);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The DeepSeek API returned a malformed response.", ex);
        }

        if (deepSeekResponse?.Choices == null || deepSeekResponse.Choices.Count == 0)
            throw new InvalidOperationException("DeepSeek returned no choices.");

        var choice = deepSeekResponse.Choices[0];

        // finish_reason == "length" means the output hit the token limit, so the JSON
        // is almost certainly truncated — treat it as a malformed response and retry.
        if (string.Equals(choice.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "DeepSeek response was truncated by the token limit (finish_reason=length).");

        var responseContent = choice.Message?.Content;
        if (string.IsNullOrWhiteSpace(responseContent))
            throw new InvalidOperationException("DeepSeek response content was empty.");

        _logger.LogInformation("Raw DeepSeek response received ({Length} chars)", responseContent.Length);

        try
        {
            var result = JsonSerializer.Deserialize<BatchAnalysisResult>(responseContent);
            return result?.Results ?? new List<PostAnalysisResult>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize DeepSeek response: {Content}",
                responseContent[..Math.Min(500, responseContent.Length)]);
            throw new InvalidOperationException("The LLM returned an unexpected response format.", ex);
        }
    }

    private static string BuildSystemPrompt()
    {
        return """
            Eres un asistente especializado en reconocer si una publicación de Instagram es el cartel/anuncio de un evento (concierto, fiesta, festival, club night, feria, exposición, obra de teatro, etc.) o no lo es.

            Para cada publicación debes determinar:
            1. **is_event** (bool): true si es un cartel de evento, false si no lo es.
            2. **title** (string | null): título del evento. Si no es evento, null.
            3. **event_date** (string | null): fecha y hora del evento en formato ISO 8601 (YYYY-MM-DDTHH:mm:ss). Si no se puede determinar una fecha concreta, null.
            4. **event_date_description** (string | null): descripción textual de la fecha (ej: "Todos los jueves", "Sábado 19 de septiembre de 2026", "Del 14 al 17 de septiembre", "14 de mayo a las 21:00"). Si no aplica, null.
            5. **summary** (string | null): resumen breve en español (máximo 2 frases) describiendo el evento. Si no es evento, null.
            6. **is_recurrent** (bool): true si el evento se repite en el tiempo (patrón semanal, o un evento que abarca varios días seguidos). false si no.
            7. **recurrence_type** (string | null): "weekly" si el evento se repite por días de la semana ("todos los jueves", "de lunes a viernes"); "daily" si ocurre cada día dentro de un rango de fechas concreto (ferias, festivales, eventos de varios días). null si no es recurrente.
            8. **recurrence_days_of_week** (array de números | null): días de la semana en los que se repite, donde 1=lunes, 2=martes, 3=miércoles, 4=jueves, 5=viernes, 6=sábado, 7=domingo. "De lunes a jueves" → [1,2,3,4]. "Todos los jueves" → [4]. null si no aplica.
            9. **recurrence_start_date** (string | null, ISO 8601 YYYY-MM-DD): fecha en la que empieza la recurrencia o el rango de días (primer día indicado en el cartel). null si no aplica.
            10. **recurrence_end_date** (string | null, ISO 8601 YYYY-MM-DD): fecha en la que termina la recurrencia o el rango de días. null si la recurrencia es indefinida ("todos los jueves" sin fecha de fin) o si no aplica.

            REGLAS PARA FECHAS APROXIMADAS Y EVENTOS CON NOMBRE PROPIO (OBLIGATORIO):
            - Si el cartel menciona un evento con nombre propio conocido ("Feria de Córdoba", "San Isidro", "WOMAD", "Fallas de Valencia", "Sónar", "Feria de Abril"...), DEBES buscar en tu conocimiento la fecha o rango de fechas real de la edición que se anuncia y rellenar event_date / recurrence_start_date / recurrence_end_date con fechas concretas. Está PROHIBIDO dejar las fechas a null cuando conoces el evento: SÍ O SÍ debes resolverlas, aunque el cartel no las diga explícitamente.
            - Si el cartel usa fechas relativas ("este sábado", "el próximo viernes", "esta semana"), resuélvelas a fechas concretas usando como referencia la fecha de publicación del post (campo datetime) y el rango de fechas solicitado por el usuario si aparece en el mensaje.
            - Si el cartel indica un rango de días concreto ("del 14 al 17 de septiembre", "de lunes 14 a jueves 17"), refleja ese rango en recurrence_start_date y recurrence_end_date.
            - Si el evento dura varios días seguidos (feria, festival, "del 23 al 30 de mayo"), usa recurrence_type "daily" con recurrence_start_date y recurrence_end_date cubriendo el rango completo.
            - Un evento recurrente sin fechas en el cartel ("todos los jueves" a secas): usa recurrence_type "weekly", recurrence_days_of_week con los días correspondientes, y recurrence_start_date con la primera fecha que puedas inferir del post o de tu conocimiento; recurrence_end_date null. Si no puedes determinar ninguna fecha en absoluto, deja is_recurrent true con los días de la semana pero todas las fechas a null (el sistema lo tratará como no verificable).
            - Si además de un patrón recurrente conoces una próxima fecha concreta, ponla también en event_date.

            REGLAS PARA DETECTAR EVENTOS:
            - El post DEBE anunciar un evento específico con fecha (concreta o recurrente).
            - Un post que solo habla de lo bien que fue un evento pasado NO es un cartel de evento.
            - Un post de agradecimiento post-evento NO es un cartel de evento.
            - Un post tipo "soon" o "próximamente" sin detalles NO es un cartel de evento (no tiene fecha ni detalles concretos).
            - Un resumen/recap de eventos pasados NO es un cartel de evento.
            - Un anuncio de merchandising/pre-order NO es un cartel de evento.
            - Un post que anuncia UN evento futuro con fecha (o recurrencia semanal clara) SÍ es un cartel de evento.

            EJEMPLO 1 — Cartel: "FERIA DE CÓRDOBA 2026 · Del 23 al 30 de mayo · Caseta Municipal":
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

            EJEMPLO 2 — Cartel: "TECHNO THURSDAYS · Todos los jueves desde el 10 de septiembre":
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

            EJEMPLO 3 — Cartel: "Del lunes 14 al jueves 17 de septiembre · Salón de actos":
            {
              "is_event": true,
              "title": "Semana cultural",
              "event_date": "2026-09-14",
              "event_date_description": "De lunes 14 a jueves 17 de septiembre de 2026",
              "summary": "Semana cultural con actividades diarias en el salón de actos.",
              "is_recurrent": true,
              "recurrence_type": "daily",
              "recurrence_days_of_week": [1,2,3,4],
              "recurrence_start_date": "2026-09-14",
              "recurrence_end_date": "2026-09-17"
            }

            EJEMPLO 4 — Cartel: "¡Gracias por venir anoche! 🔥" (agradecimiento post-evento):
            {
              "is_event": false,
              "title": null,
              "event_date": null,
              "event_date_description": null,
              "summary": null,
              "is_recurrent": false,
              "recurrence_type": null,
              "recurrence_days_of_week": null,
              "recurrence_start_date": null,
              "recurrence_end_date": null
            }

            Responde ÚNICAMENTE con un objeto JSON con esta estructura (un elemento del array "results" por cada publicación, en el mismo orden):
            {
              "results": [
                {
                  "is_event": true/false,
                  "title": "Título del evento" | null,
                  "event_date": "2026-09-19T22:00:00" | null,
                  "event_date_description": "Sábado 19 de septiembre" | null,
                  "summary": "Breve resumen en español" | null,
                  "is_recurrent": true/false,
                  "recurrence_type": "weekly" | "daily" | null,
                  "recurrence_days_of_week": [1,2,3,4,5,6,7] | null,
                  "recurrence_start_date": "2026-09-14" | null,
                  "recurrence_end_date": "2026-09-17" | null
                }
              ]
            }
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
            sb.AppendLine($"caption: {TruncateForPrompt(post.Caption, 1000)}");
            sb.AppendLine($"datetime: {(post.Datetime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "null")}");
            sb.AppendLine($"url: {post.Url}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string TruncateForPrompt(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return "(vacío)";
        return text.Length <= maxChars ? text : text[..maxChars] + "...";
    }
}
