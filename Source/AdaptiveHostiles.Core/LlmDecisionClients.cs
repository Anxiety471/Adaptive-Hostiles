using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AdaptiveHostiles.Core;

/// <summary>
/// Base for general-purpose chat models. It renders the typed Choice questions into a prompt,
/// asks for a JSON object of choice IDs, and returns a document shaped like Jev's reply so the
/// planners need no provider-specific code. A choice not listed in a question's criteria is
/// dropped, so the planners fall back exactly as they do for an unknown Jev choice.
/// </summary>
public abstract class LlmChoiceClient : IJevClient
{
    private const string System =
        "You are a decision engine for a game AI. Answer every question by choosing exactly one " +
        "listed option ID. Reply with only a JSON object mapping each question name to its chosen " +
        "option ID, e.g. {\"scale\":\"skirmish\"}. No prose, no code fences.";

    public async Task<JsonDocument> DecideAsync(
        object state, IReadOnlyDictionary<string, object> questions, CancellationToken cancellationToken)
    {
        if (questions.Count == 0)
            throw new ArgumentException("At least one decision question is required", nameof(questions));

        var user = JsonSerializer.Serialize(new { state, questions });
        var text = await CompleteAsync(System, user, cancellationToken).ConfigureAwait(false);
        return BuildAnswers(text, questions);
    }

    protected abstract Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken);

    internal static JsonDocument BuildAnswers(string modelText, IReadOnlyDictionary<string, object> questions)
    {
        var picked = ParseObject(modelText);
        var answers = new Dictionary<string, object>();
        foreach (var (name, question) in questions)
        {
            if (picked is null || !picked.TryGetValue(name, out var id) || id is null) continue;
            using var q = JsonSerializer.SerializeToDocument(question);
            if (q.RootElement.ValueKind == JsonValueKind.Object &&
                q.RootElement.TryGetProperty("criteria", out var criteria) &&
                criteria.ValueKind == JsonValueKind.Object && criteria.TryGetProperty(id, out _))
                answers[name] = new { type = "choice", choice = id };
        }
        return JsonSerializer.SerializeToDocument(new { answers });
    }

    private static Dictionary<string, string?>? ParseObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)], new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var map = new Dictionary<string, string?>();
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) map[p.Name] = p.Value.GetString();
            return map;
        }
        catch (JsonException) { return null; }
    }

    protected static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 24 }, ct)
            .ConfigureAwait(false);
    }
}

/// <summary>Anthropic Messages API. Never log the API key.</summary>
public sealed class AnthropicClient : LlmChoiceClient
{
    public const string OfficialEndpoint = "https://api.anthropic.com/v1/messages";
    public const string DefaultModel = "claude-haiku-5-5";
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public AnthropicClient(HttpClient http, string apiKey, string? model = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("An Anthropic API key is required", nameof(apiKey));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
    }

    protected override async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OfficialEndpoint);
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(new
        {
            model = _model,
            max_tokens = 256,
            system,
            messages = new[] { new { role = "user", content = user } }
        });
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        using var doc = await ReadAsync(response, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (block.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    sb.Append(t.GetString());
        return sb.ToString();
    }
}

/// <summary>
/// Any OpenAI-compatible /chat/completions endpoint: OpenAI, OpenRouter, or a local server such as
/// Ollama or LM Studio (base URL like http://localhost:11434/v1, API key optional there).
/// </summary>
public sealed class OpenAiCompatibleClient : LlmChoiceClient
{
    public const string OpenAiBaseUrl = "https://api.openai.com/v1";
    public const string DefaultModel = "gpt-4o-mini";
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly string _url;

    public OpenAiCompatibleClient(HttpClient http, string? apiKey, string? baseUrl = null, string? model = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        _url = (string.IsNullOrWhiteSpace(baseUrl) ? OpenAiBaseUrl : baseUrl).TrimEnd('/') + "/chat/completions";
    }

    protected override async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _url);
        if (_apiKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = JsonContent.Create(new
        {
            model = _model,
            max_tokens = 256,
            temperature = 0.2,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        });
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        using var doc = await ReadAsync(response, ct).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var msg) &&
            msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";
        return "";
    }
}
