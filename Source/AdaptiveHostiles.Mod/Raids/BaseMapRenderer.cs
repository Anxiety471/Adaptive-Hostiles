using AdaptiveHostiles.Core;
using RimWorld;
using Verse;

namespace AdaptiveHostiles.Mod.Raids;

/// <summary>
/// Draws the colony base as a small top-down picture for Jev (see <see cref="BaseVision.Legend"/>). Game thread only.
/// The region is the colony's buildings and colonists plus a margin, so the outer wall is in frame.
/// </summary>
public static class BaseMapRenderer
{
    private const int Margin = 8;
    private const int MinSide = 64;
    private const int MaxSide = 200;
    private const int TargetPixels = 1024;

    private static readonly (byte R, byte G, byte B)[] Palette =
    {
        (0, 0, 0),          // 0 unknown or fogged
        (116, 150, 92),     // 1 open ground outside
        (222, 205, 170),    // 2 floor under a roof
        (40, 40, 40),       // 3 wall
        (235, 120, 20),     // 4 door
        (110, 85, 60),      // 5 natural rock
        (20, 70, 255),      // 6 defender
        (230, 0, 0)         // 7 turret
    };

    public static BaseImage? Render(Map map)
    {
        var colonists = map.mapPawns.FreeColonistsSpawned.ToList();
        var buildings = map.listerBuildings.allBuildingsColonist;
        var xs = colonists.Select(p => p.Position.x).Concat(buildings.Select(b => b.Position.x)).ToList();
        var zs = colonists.Select(p => p.Position.z).Concat(buildings.Select(b => b.Position.z)).ToList();
        if (xs.Count == 0) return null;

        var (originX, wide) = Span(xs.Min(), xs.Max(), map.Size.x);
        var (originZ, high) = Span(zs.Min(), zs.Max(), map.Size.z);
        var cells = new byte[wide * high];
        for (var row = 0; row < high; row++)
        for (var col = 0; col < wide; col++)
            cells[row * wide + col] = Classify(map, new IntVec3(originX + col, 0, originZ + high - 1 - row));

        foreach (var p in colonists) Mark(cells, wide, high, originX, originZ, p.Position, 6);
        foreach (var t in buildings.OfType<Building_Turret>()) Mark(cells, wide, high, originX, originZ, t.Position, 7);

        var scale = Math.Max(4, TargetPixels / Math.Max(wide, high));
        return new BaseImage(PngWriter.EncodeIndexed(cells, wide, high, scale, Palette), originX, originZ, wide, high);
    }

    // A window around [min, max] with a margin, at least MinSide and at most MaxSide cells, shifted to stay on the map.
    private static (int Origin, int Size) Span(int min, int max, int mapSize)
    {
        var size = Math.Clamp(max - min + 1 + 2 * Margin, MinSide, Math.Min(MaxSide, mapSize));
        var origin = Math.Clamp((min + max) / 2 - size / 2, 0, mapSize - size);
        return (origin, size);
    }

    private static byte Classify(Map map, IntVec3 c)
    {
        if (!c.InBounds(map) || c.Fogged(map)) return 0;
        var edifice = c.GetEdifice(map);
        if (edifice is Building_Door) return 4;
        if (edifice is not null && edifice.def.passability == Traversability.Impassable)
            return edifice.def.building is { isNaturalRock: true } ? (byte)5 : (byte)3;
        return map.roofGrid.Roofed(c) ? (byte)2 : (byte)1;
    }

    // A 3 by 3 block so a defender or turret stays visible once the picture is scaled to Jev's size.
    private static void Mark(byte[] cells, int wide, int high, int originX, int originZ, IntVec3 at, byte value)
    {
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        {
            int col = at.x + dx - originX, row = high - 1 - (at.z + dz - originZ);
            if (col >= 0 && col < wide && row >= 0 && row < high) cells[row * wide + col] = value;
        }
    }
}
