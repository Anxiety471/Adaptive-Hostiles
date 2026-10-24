using AdaptiveHostiles.Core;
using RimWorld;
using Verse;

namespace AdaptiveHostiles.Mod.Raids;

/// <summary>
/// Samples weapon/apparel defs into Core gear candidates, lets Core pick a seeded loadout within the
/// faction's tech level, then equips the pawn with high-quality versions of it.
/// </summary>
public static class OperativeGearApplier
{
    public static void Apply(Pawn pawn, Faction faction, ulong seed)
    {
        if (pawn.equipment is null || pawn.apparel is null) return;

        var defs = new Dictionary<string, ThingDef>(StringComparer.Ordinal);
        var candidates = Sample(pawn, defs);
        var loadout = OperativeGear.Roll(candidates, (int)faction.def.techLevel, seed,
            preferRanged: !pawn.WorkTagIsDisabled(WorkTags.Violent));
        if (loadout is null) return;

        Equip(pawn, defs[loadout.Weapon.DefName], loadout.Weapon.Quality, weapon: true);
        foreach (var pick in new[] { loadout.BodyArmor, loadout.Helmet })
            if (pick is not null)
                Equip(pawn, defs[pick.DefName], pick.Quality, weapon: false);

        Log.Message($"[Adaptive Hostiles] Operative {pawn.LabelShort} gear: {loadout.Weapon.DefName} ({loadout.Weapon.Quality})" +
                    $"{(loadout.BodyArmor is null ? "" : ", " + loadout.BodyArmor.DefName)}" +
                    $"{(loadout.Helmet is null ? "" : ", " + loadout.Helmet.DefName)}.");
    }

    private static List<GearCandidate> Sample(Pawn pawn, Dictionary<string, ThingDef> defs)
    {
        var list = new List<GearCandidate>();
        foreach (var def in DefDatabase<ThingDef>.AllDefs)
        {
            if (def.IsWeapon && def.equipmentType == EquipmentType.Primary && !def.destroyOnDrop &&
                def.tradeability != Tradeability.None && def.IsMeleeWeapon != def.IsRangedWeapon)
            {
                // Skip one-shot/disposable weapons (grenades, launchers with no reload) and unfinished mod defs.
                if (def.Verbs is null || def.Verbs.Count == 0) continue;
                var dps = def.IsRangedWeapon
                    ? def.GetStatValueAbstract(StatDefOf.RangedWeapon_Cooldown) > 0f
                        ? def.Verbs[0].defaultProjectile?.projectile?.GetDamageAmount(null) /
                          (def.GetStatValueAbstract(StatDefOf.RangedWeapon_Cooldown) + def.Verbs[0].warmupTime) ?? 0f
                        : 0f
                    : def.tools?.Sum(t => t.power) ?? 0f;
                var slot = def.IsRangedWeapon ? GearSlot.RangedWeapon : GearSlot.MeleeWeapon;
                Add(def, slot, dps);
            }
            else if (def.IsApparel && def.apparel is not null && def.tradeability != Tradeability.None &&
                     ApparelUtility.HasPartsToWear(pawn, def))
            {
                var layers = def.apparel.layers;
                var parts = def.apparel.bodyPartGroups;
                var armor = def.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp) +
                            def.GetStatValueAbstract(StatDefOf.ArmorRating_Blunt);
                if (armor <= 0f) continue;
                if (layers.Contains(ApparelLayerDefOf.Shell) && parts.Contains(BodyPartGroupDefOf.Torso))
                    Add(def, GearSlot.BodyArmor, armor);
                else if (layers.Contains(ApparelLayerDefOf.Overhead) && parts.Contains(BodyPartGroupDefOf.UpperHead))
                    Add(def, GearSlot.Helmet, armor);
            }
        }
        return list;

        void Add(ThingDef def, GearSlot slot, float power)
        {
            defs[def.defName] = def;
            list.Add(new GearCandidate(def.defName, slot, (int)def.techLevel, power));
        }
    }

    private static void Equip(Pawn pawn, ThingDef def, GearQuality quality, bool weapon)
    {
        ThingDef? stuff = def.MadeFromStuff ? BestStuff(def) : null;
        var thing = ThingMaker.MakeThing(def, stuff);
        thing.TryGetComp<CompQuality>()?.SetQuality(ToQuality(quality), ArtGenerationContext.Outsider);

        if (weapon && thing is ThingWithComps weaponThing)
        {
            pawn.equipment.DestroyAllEquipment();
            pawn.equipment.AddEquipment(weaponThing);
        }
        else if (thing is Apparel apparel)
        {
            foreach (var worn in pawn.apparel.WornApparel.ToList())
                if (!ApparelUtility.CanWearTogether(worn.def, def, pawn.RaceProps.body))
                    pawn.apparel.Remove(worn);
            pawn.apparel.Wear(apparel, dropReplacedApparel: false);
        }
    }

    private static ThingDef? BestStuff(ThingDef def)
    {
        // Armor prefers the highest sharp armor material; weapons prefer the sharpest material.
        var stat = def.IsApparel ? StatDefOf.StuffPower_Armor_Sharp : StatDefOf.SharpDamageMultiplier;
        return GenStuff.AllowedStuffsFor(def)
            .OrderByDescending(s => s.GetStatValueAbstract(stat))
            .ThenBy(s => s.defName, StringComparer.Ordinal)
            .FirstOrDefault() ?? GenStuff.DefaultStuffFor(def);
    }

    private static QualityCategory ToQuality(GearQuality q) => q switch
    {
        GearQuality.Good => QualityCategory.Good,
        GearQuality.Excellent => QualityCategory.Excellent,
        GearQuality.Masterwork => QualityCategory.Masterwork,
        _ => QualityCategory.Legendary
    };
}
