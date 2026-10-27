using System.Text.Json;

namespace AdaptiveHostiles.Core;

/// <summary>A top-down picture of the colony base and the map cells it covers (image row 0 is the highest z).</summary>
public sealed record BaseImage(byte[] Png, int OriginX, int OriginZ, int CellsWide, int CellsHigh);

/// <summary>Jev's reading of a base picture: is it sealed, is there an outer wall, and where is the weakest stretch of it.</summary>
public sealed record BaseVisionResult(
    double Sealed, double HasWall, double Left, double Top, double Right, double Bottom, double BoxConfidence);

public sealed record BaseVisionReading(BaseImage Image, BaseVisionResult Result)
{
    public const double SealedMin = 0.7;
    public const double HasWallMin = 0.7;
    public const double BoxConfidenceMin = 0.5;

    /// <summary>
    /// The map cell at the centre of the weakest wall, when the base is sealed, has an outer wall and the box is
    /// trustworthy. An open base needs no wall hint: the normal open-base logic already covers it.
    /// </summary>
    public bool TryWallCell(out int x, out int z)
    {
        x = z = 0;
        var r = Result;
        if (r.Sealed < SealedMin || r.HasWall < HasWallMin || r.BoxConfidence < BoxConfidenceMin) return false;
        if (r.Left < 0 || r.Top < 0 || r.Right > 1 || r.Bottom > 1 || r.Left >= r.Right || r.Top >= r.Bottom) return false;
        var cx = (r.Left + r.Right) / 2;
        var cy = (r.Top + r.Bottom) / 2;
        x = Image.OriginX + Math.Clamp((int)(cx * Image.CellsWide), 0, Image.CellsWide - 1);
        z = Image.OriginZ + Image.CellsHigh - 1 - Math.Clamp((int)(cy * Image.CellsHigh), 0, Image.CellsHigh - 1);
        return true;
    }
}

/// <summary>The image questions: three judgments about the base picture, combined in code by <see cref="BaseVisionReading"/>.</summary>
public static class BaseVision
{
    public const string ImageId = "base-map";
    public const string Legend =
        "Top-down map of a colony base. Dark gray is wall, orange is door, brown is natural rock, green is open ground outside, " +
        "tan is floor under a roof, blue squares are colonist defenders, red squares are turrets, black is unknown.";

    public static (object State, IReadOnlyDictionary<string, object> Questions, IReadOnlyDictionary<string, object> Assets) Build(BaseImage image) => (
        new Dictionary<string, object> { ["map"] = ImageId, ["legend"] = Legend },
        new Dictionary<string, object>
        {
            ["sealed"] = new
            {
                type = "noul",
                instructions = "Is the colony base in `map` fully enclosed by walls and doors, with no gap an attacker could walk through?"
            },
            ["has_wall"] = new
            {
                type = "noul",
                instructions = "Is there an outer wall of the colony base visible in `map`?"
            },
            ["weak_wall"] = new
            {
                type = "box",
                instructions = "the stretch of outer wall of the colony base in `map` that is farthest from any blue defender or red turret"
            }
        },
        new Dictionary<string, object>
        {
            [ImageId] = new { type = "image", format = "png", data_base64 = Convert.ToBase64String(image.Png) }
        });

    public static async Task<BaseVisionReading> AskAsync(IImageDecisionClient client, BaseImage image, CancellationToken token)
    {
        var (state, questions, assets) = Build(image);
        using var doc = await client.DecideAsync(state, questions, assets, token).ConfigureAwait(false);
        return new BaseVisionReading(image, Parse(doc.RootElement));
    }

    public static BaseVisionResult Parse(JsonElement root)
    {
        if (!root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("No answers in the vision response");
        var box = Answer(answers, "weak_wall", "box");
        return new BaseVisionResult(
            Number(Answer(answers, "sealed", "noul")), Number(Answer(answers, "has_wall", "noul")),
            Number(Edge(box, "left")), Number(Edge(box, "top")), Number(Edge(box, "right")), Number(Edge(box, "bottom")),
            Number(Answer(answers, "weak_wall", "confidence")));
    }

    private static JsonElement Answer(JsonElement answers, string id, string field) =>
        answers.TryGetProperty(id, out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty(field, out var f)
            ? f
            : throw new InvalidDataException($"Missing {id}.{field} in the vision response");

    private static JsonElement Edge(JsonElement box, string name) =>
        box.ValueKind == JsonValueKind.Object && box.TryGetProperty(name, out var e)
            ? e
            : throw new InvalidDataException($"Missing box edge {name}");

    private static double Number(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) && double.IsFinite(d)
            ? d
            : throw new InvalidDataException("Expected a number in the vision response");
}
