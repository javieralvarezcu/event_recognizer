using System.Text;
using System.Text.Json;
using EventRecognizer.Api.Models;

namespace EventRecognizer.Api.Services;

public class DeepSeekService : IDeepSeekService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeepSeekService> _logger;
    private const string DeepSeekApiUrl = "https://api.deepseek.com/v1/chat/completions";

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
        var deepSeekResponse = JsonSerializer.Deserialize<DeepSeekResponse>(responseBody);

        if (deepSeekResponse?.Choices == null || deepSeekResponse.Choices.Count == 0)
        {
            _logger.LogWarning("DeepSeek returned no choices");
            return new List<PostAnalysisResult>();
        }

        var responseContent = deepSeekResponse.Choices[0].Message?.Content;
        if (string.IsNullOrWhiteSpace(responseContent))
        {
            _logger.LogWarning("DeepSeek response content was empty");
            return new List<PostAnalysisResult>();
        }

        _logger.LogInformation("Raw DeepSeek response received ({Length} chars)", responseContent.Length);

        try
        {
            var result = JsonSerializer.Deserialize<BatchAnalysisResult>(responseContent);
            return result?.Results ?? new List<PostAnalysisResult>();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize DeepSeek response: {Content}",
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
