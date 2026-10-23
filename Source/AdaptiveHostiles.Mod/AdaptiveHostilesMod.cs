using HarmonyLib;
using Verse;

namespace AdaptiveHostiles.Mod;

/// <summary>
/// Lifecycle bootstrap only. We intentionally do not patch hostile raids until
/// the incident/roster adapter is implemented and validated against RimWorld 1.7.
/// </summary>
public sealed class AdaptiveHostilesMod : Mod
{
    public AdaptiveHostilesMod(ModContentPack content) : base(content)
    {
        new Harmony("anxiety471.adaptivehostiles").PatchAll();
        Log.Message("[Adaptive Hostiles] Pre-alpha bootstrap loaded; gameplay hooks are not yet active.");
    }
}
