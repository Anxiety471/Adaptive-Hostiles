using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AdaptiveHostiles.Core;

/// <summary>
/// Minimal official TypeSafe System One client. Use a shared HttpClient, never call
/// this on RimWorld's main thread, and never log the supplied API key.
/// </summary>
public sealed class JevClient : IJevClient, IImageDecisionClient
{
    public const string OfficialEndpoint = "https://api.typesafe.ai/v1/systemone";
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public JevClient(HttpClient http, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("A TypeSafe API key is required", nameof(apiKey));

        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = apiKey;
    }

    public const string TextModel = "jev-latest";
    /// <summary>Images and Box questions need this model and the images header.</summary>
    public const string ImageModel = "jev-1.14.0";
    public const string ImagesHeader = "TypeSafe-Danger-Zone-Images";

    public Task<JsonDocument> DecideAsync(
        object state, IReadOnlyDictionary<string, object> questions, CancellationToken cancellationToken) =>
        PostAsync(new { model = TextModel, state, questions }, images: false, questions.Count, cancellationToken);

    public Task<JsonDocument> DecideAsync(
        object state, IReadOnlyDictionary<string, object> questions, IReadOnlyDictionary<string, object> assets,
        CancellationToken cancellationToken) =>
        PostAsync(new { model = ImageModel, state, assets, questions }, images: true, questions.Count, cancellationToken);

    private async Task<JsonDocument> PostAsync(object body, bool images, int questionCount, CancellationToken cancellationToken)
    {
        if (questionCount == 0)
            throw new ArgumentException("At least one decision question is required", "questions");

        using var request = new HttpRequestMessage(HttpMethod.Post, OfficialEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        if (images) request.Headers.Add(ImagesHeader, "true");
        request.Content = JsonContent.Create(body);

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions
        {
            MaxDepth = 24
        }, cancellationToken).ConfigureAwait(false);
    }
}

public interface IJevClient
{
    Task<JsonDocument> DecideAsync(
        object state, IReadOnlyDictionary<string, object> questions, CancellationToken cancellationToken);
}

/// <summary>A decision client that can read images (Jev only). Assets map an id used in the state to an image.</summary>
public interface IImageDecisionClient
{
    Task<JsonDocument> DecideAsync(
        object state, IReadOnlyDictionary<string, object> questions, IReadOnlyDictionary<string, object> assets,
        CancellationToken cancellationToken);
}

public static class JevAnswers
{
    public static string? Choice(JsonElement root, string name)
    {
        if (!root.TryGetProperty("answers", out var answers) ||
            answers.ValueKind != JsonValueKind.Object ||
            !answers.TryGetProperty(name, out var answer) ||
            answer.ValueKind != JsonValueKind.Object ||
            !answer.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            type.GetString() != "choice" ||
            !answer.TryGetProperty("choice", out var choice) ||
            choice.ValueKind != JsonValueKind.String)
            return null;

        return choice.GetString();
    }
}
