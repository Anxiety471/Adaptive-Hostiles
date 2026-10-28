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
    /// <summary>Game ticks between squad decisions (60 = about one second).</summary>
    public int SquadInterval = 120;
    /// <summary>Jev reads a picture of the base to pick the weakest wall; on unless switched off.</summary>
    public bool BaseImages = true;

    public override void ExposeData()
    {
        Scribe_Values.Look(ref Provider, "provider", "auto");
        Scribe_Values.Look(ref JevKey, "jevKey", "");
        Scribe_Values.Look(ref AnthropicKey, "anthropicKey", "");
        Scribe_Values.Look(ref OpenAiKey, "openAiKey", "");
        Scribe_Values.Look(ref OpenAiBaseUrl, "openAiBaseUrl", "");
        Scribe_Values.Look(ref Model, "model", "");
        // New key: the old "squadInterval" held the previous 60-tick default, which would have kept everyone on one second.
        Scribe_Values.Look(ref SquadInterval, "squadIntervalV2", 120);
        Scribe_Values.Look(ref BaseImages, "baseImages", true);
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
