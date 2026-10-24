using AdaptiveHostiles.Core;
using Verse;

namespace AdaptiveHostiles.Mod;

/// <summary>Saved with RimWorld's mod settings (Config folder, plain text).</summary>
public sealed class ModSettingsData : ModSettings
{
    public string Provider = "auto";
    public string JevKey = "";
    public string AnthropicKey = "";
    public string OpenAiKey = "";
    public string OpenAiBaseUrl = "";
    public string Model = "";

    public override void ExposeData()
    {
        Scribe_Values.Look(ref Provider, "provider", "auto");
        Scribe_Values.Look(ref JevKey, "jevKey", "");
        Scribe_Values.Look(ref AnthropicKey, "anthropicKey", "");
        Scribe_Values.Look(ref OpenAiKey, "openAiKey", "");
        Scribe_Values.Look(ref OpenAiBaseUrl, "openAiBaseUrl", "");
        Scribe_Values.Look(ref Model, "model", "");
    }

    public ProviderSettings ToProviderSettings() => new()
    {
        Provider = DecisionProviders.ParseProvider(Provider),
        JevKey = JevKey ?? "",
        AnthropicKey = AnthropicKey ?? "",
        OpenAiKey = OpenAiKey ?? "",
        OpenAiBaseUrl = OpenAiBaseUrl ?? "",
        Model = Model ?? ""
    };
}
