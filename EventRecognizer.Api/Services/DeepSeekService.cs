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

            var chunkResults = await AnalyzeChunkWithRetriesAsync(chunk, apiKey, ct);
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
        CancellationToken ct)
    {
        var attempt = 1;
        while (true)
        {
            try
            {
                return await AnalyzeChunkAsync(chunk, apiKey, ct);
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

            await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            attempt++;
        }
    }

    private static bool IsTransientHttpError(HttpRequestException ex) =>
        ex.StatusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable;

    private async Task<List<PostAnalysisResult>> AnalyzeChunkAsync(
        List<InstagramPost> posts,
        string apiKey,
        CancellationToken ct)
    {
        var systemPrompt = BuildSystemPrompt();
        var userPrompt = BuildUserPrompt(posts);

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
            Eres un asistente especializado en reconocer si una publicación de Instagram es el cartel/anuncio de un evento (concierto, fiesta, festival, club night, exposición, obra de teatro, etc.) o no lo es.

            Para cada publicación debes determinar:
            1. **is_event** (bool): true si es un cartel de evento, false si no lo es.
            2. **title** (string | null): título del evento. Si no es evento, null.
            3. **event_date** (string | null): fecha y hora del evento en formato ISO 8601 (YYYY-MM-DDTHH:mm:ss). Si no se puede determinar una fecha concreta, null.
            4. **event_date_description** (string | null): descripción textual de la fecha (ej: "Todos los jueves", "Sábado 19 de septiembre de 2026", "14 de mayo a las 21:00"). Si no aplica, null.
            5. **summary** (string | null): resumen breve en español (máximo 2 frases) describiendo el evento. Si no es evento, null.

            Reglas para detectar eventos:
            - El post DEBE anunciar un evento específico con fecha (concreta o recurrente).
            - Un post que solo habla de lo bien que fue un evento pasado NO es un cartel de evento.
            - Un post de agradecimiento post-evento NO es un cartel de evento.
            - Un post tipo "soon" o "próximamente" sin detalles NO es un cartel de evento (no tiene fecha ni detalles concretos).
            - Un resumen/recap de eventos pasados NO es un cartel de evento.
            - Un anuncio de merchandising/pre-order NO es un cartel de evento.
            - Un post que anuncia UN evento futuro con fecha (o recurrencia semanal clara) SÍ es un cartel de evento.

            Responde ÚNICAMENTE con un objeto JSON con esta estructura:
            {
              "results": [
                {
                  "is_event": true/false,
                  "title": "Título del evento" | null,
                  "event_date": "2026-09-19T22:00:00" | null,
                  "event_date_description": "Sábado 19 de septiembre" | null,
                  "summary": "Breve resumen en español" | null
                }
              ]
            }
            """;
    }

    private static string BuildUserPrompt(List<InstagramPost> posts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Analiza las siguientes publicaciones de Instagram (una por cada elemento del array):");
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

    private static string TruncateForPrompt(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return "(vacío)";
        return text.Length <= maxChars ? text : text[..maxChars] + "...";
    }
}
