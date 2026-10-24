namespace AdaptiveHostiles.Core;

public enum DecisionProvider { Auto, Jev, Anthropic, OpenAiCompatible, Local }

/// <summary>
/// Chooses the decision model. Environment variables (never commit keys):
/// ADAPTIVE_HOSTILES_PROVIDER = auto | jev | anthropic | openai | local (default auto),
/// ADAPTIVE_HOSTILES_MODEL and ADAPTIVE_HOSTILES_ENDPOINT override the model / OpenAI-compatible base URL,
/// keys: TYPESAFE_API_KEY, ANTHROPIC_API_KEY, OPENAI_API_KEY.
/// Auto picks the first provider with a key (Jev, Anthropic, OpenAI); with none, or "local",
/// no client is returned and planners use their deterministic fallback.
/// </summary>
public static class DecisionProviders
{
    public static DecisionProvider ParseProvider(string? text) =>
        (text ?? "").Trim().ToLowerInvariant() switch
        {
            "jev" => DecisionProvider.Jev,
            "anthropic" or "claude" => DecisionProvider.Anthropic,
            "openai" or "openai-compatible" => DecisionProvider.OpenAiCompatible,
            "local" or "none" or "off" => DecisionProvider.Local,
            _ => DecisionProvider.Auto
        };

    public static IJevClient? Create(HttpClient http, Func<string, string?> env)
    {
        var provider = ParseProvider(env("ADAPTIVE_HOSTILES_PROVIDER"));
        var jev = Key(env, "TYPESAFE_API_KEY");
        var anthropic = Key(env, "ANTHROPIC_API_KEY");
        var openai = Key(env, "OPENAI_API_KEY");
        var model = env("ADAPTIVE_HOSTILES_MODEL");
        var endpoint = env("ADAPTIVE_HOSTILES_ENDPOINT");

        if (provider == DecisionProvider.Auto)
            provider = jev is not null ? DecisionProvider.Jev
                : anthropic is not null ? DecisionProvider.Anthropic
                : openai is not null ? DecisionProvider.OpenAiCompatible
                : DecisionProvider.Local;

        return provider switch
        {
            DecisionProvider.Jev when jev is not null => new JevClient(http, jev),
            DecisionProvider.Anthropic when anthropic is not null => new AnthropicClient(http, anthropic, model),
            // A custom endpoint (e.g. a local server) may need no key.
            DecisionProvider.OpenAiCompatible when openai is not null || !string.IsNullOrWhiteSpace(endpoint) =>
                new OpenAiCompatibleClient(http, openai, endpoint, model),
            _ => null
        };
    }

    private static string? Key(Func<string, string?> env, string name) =>
        string.IsNullOrWhiteSpace(env(name)) ? null : env(name);
}
