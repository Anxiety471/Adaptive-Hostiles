using HarmonyLib;
using Verse;

namespace AdaptiveHostiles.Mod;

/// <summary>
/// Lifecycle bootstrap. Harmony patches (see Raids/RaidPatches.cs) are applied by PatchAll;
/// they are written against RimWorld 1.6-era APIs and still need validation against 1.7.
/// </summary>
public sealed class AdaptiveHostilesMod : Verse.Mod
{
    public AdaptiveHostilesMod(ModContentPack content) : base(content)
    {
        new Harmony("anxiety471.adaptivehostiles").PatchAll();
        Log.Message("[Adaptive Hostiles] Pre-alpha loaded; hostile raid composition hook is active.");
    }
}
