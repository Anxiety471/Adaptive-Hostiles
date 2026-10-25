using AdaptiveHostiles.Core;
using HarmonyLib;
using Verse;

namespace AdaptiveHostiles.Mod;

/// <summary>
/// Lifecycle bootstrap. Harmony patches (see Raids/RaidPatches.cs) are applied by PatchAll;
/// they are written against RimWorld 1.6-era APIs and still need validation against 1.7.
/// </summary>
public sealed class AdaptiveHostilesMod : Verse.Mod
{
    private readonly ModSettingsData _data;

    /// <summary>Player-entered provider settings; read once when the decision client is created.</summary>
    public static ProviderSettings Providers { get; private set; } = new();
    /// <summary>Game ticks between squad decisions, from the mod settings page.</summary>
    public static int SquadIntervalTicks { get; private set; } = 60;

    public AdaptiveHostilesMod(ModContentPack content) : base(content)
    {
        _data = GetSettings<ModSettingsData>();
        Providers = _data.ToProviderSettings();
        SquadIntervalTicks = _data.SquadInterval;
        new Harmony("anxiety471.adaptivehostiles").PatchAll();
        Log.Message("[Adaptive Hostiles] Pre-alpha loaded; hostile raid composition hook is active.");
    }

    public override string SettingsCategory() => "Adaptive Hostiles";

    public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
    {
        SettingsWindow.Draw(inRect, _data);
        Providers = _data.ToProviderSettings();
    }
}
