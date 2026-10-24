namespace AdaptiveHostiles.Core;

/// <summary>
/// Player-entered decision model configuration (the in-game Mod Settings page). Blank fields,
/// and a provider of Auto, defer to the environment so env-var setups keep working.
/// </summary>
public sealed record ProviderSettings
{
    public DecisionProvider Provider { get; init; } = DecisionProvider.Auto;
    public string JevKey { get; init; } = "";
    public string AnthropicKey { get; init; } = "";
    public string OpenAiKey { get; init; } = "";
    public string OpenAiBaseUrl { get; init; } = "";
    public string Model { get; init; } = "";

    /// <summary>Lookup for <see cref="DecisionProviders.Create"/>: these settings first, then <paramref name="fallback"/>.</summary>
    public Func<string, string?> ToLookup(Func<string, string?> fallback) => name =>
    {
        var own = name switch
        {
            "ADAPTIVE_HOSTILES_PROVIDER" => Provider == DecisionProvider.Auto ? "" : ProviderName(Provider),
            "TYPESAFE_API_KEY" => JevKey,
            "ANTHROPIC_API_KEY" => AnthropicKey,
            "OPENAI_API_KEY" => OpenAiKey,
            "ADAPTIVE_HOSTILES_ENDPOINT" => OpenAiBaseUrl,
            "ADAPTIVE_HOSTILES_MODEL" => Model,
            _ => ""
        };
        return string.IsNullOrWhiteSpace(own) ? fallback(name) : own.Trim();
    };

    public static string ProviderName(DecisionProvider p) => p switch
    {
        DecisionProvider.Jev => "jev",
        DecisionProvider.Anthropic => "anthropic",
        DecisionProvider.OpenAiCompatible => "openai",
        DecisionProvider.Local => "local",
        _ => "auto"
    };
}
