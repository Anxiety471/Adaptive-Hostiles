using AdaptiveHostiles.Core;
using UnityEngine;
using Verse;

namespace AdaptiveHostiles.Mod;

/// <summary>Options > Mod settings > Adaptive Hostiles. Keys show as bullets unless revealed.</summary>
public static class SettingsWindow
{
    private static readonly HashSet<string> Revealed = new();

    private static readonly (string Value, string Label)[] Providers =
    {
        ("auto", "Automatic (first key entered)"),
        ("jev", "TypeSafe Jev"),
        ("anthropic", "Anthropic (Claude)"),
        ("openai", "OpenAI-compatible (OpenAI, OpenRouter, local server)"),
        ("local", "Local only (no online model)")
    };

    public static void Draw(Rect inRect, ModSettingsData data)
    {
        var list = new Listing_Standard();
        list.Begin(inRect);

        list.Label("Decision model for raids and Operatives. With no key, the mod uses its built-in local logic.");
        var current = Array.Find(Providers, p => p.Value == data.Provider);
        if (current.Value is null) current = Providers[0];
        if (list.ButtonTextLabeled("Provider", current.Label))
        {
            var options = new List<FloatMenuOption>();
            foreach (var p in Providers)
            {
                var value = p.Value;
                options.Add(new FloatMenuOption(p.Label, () => data.Provider = value));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        list.GapLine();
        KeyField(list, "TypeSafe Jev key", ref data.JevKey, "jev");
        KeyField(list, "Anthropic key", ref data.AnthropicKey, "anthropic");
        KeyField(list, "OpenAI-compatible key (optional for local servers)", ref data.OpenAiKey, "openai");

        list.GapLine();
        list.Label("OpenAI-compatible base URL (blank = OpenAI), e.g. http://localhost:11434/v1");
        data.OpenAiBaseUrl = list.TextEntry(data.OpenAiBaseUrl ?? "");
        list.Label("Model override (blank = provider default)");
        data.Model = list.TextEntry(data.Model ?? "");

        list.GapLine();
        list.Label("Keys are stored unencrypted in your RimWorld Config folder; do not share that file. " +
                   "Changes apply the next time the game starts. Environment variables are still used for any blank field.");
        list.End();
    }

    private static void KeyField(Listing_Standard list, string label, ref string value, string id)
    {
        value ??= "";
        list.Label(label);
        var row = list.GetRect(30f);
        var field = new Rect(row.x, row.y, row.width - 260f, row.height);
        var shown = Revealed.Contains(id);

        if (shown)
            value = Widgets.TextField(field, value);
        else
            Widgets.Label(field, value.Length == 0 ? "(not set)" : new string('*', Math.Min(value.Length, 24)));

        var x = field.xMax + 6f;
        if (Widgets.ButtonText(new Rect(x, row.y, 80f, row.height), "Paste"))
            value = (GUIUtility.systemCopyBuffer ?? "").Trim();
        if (Widgets.ButtonText(new Rect(x + 84f, row.y, 80f, row.height), shown ? "Hide" : "Edit"))
        {
            if (!Revealed.Add(id)) Revealed.Remove(id);
        }
        if (Widgets.ButtonText(new Rect(x + 168f, row.y, 80f, row.height), "Clear"))
            value = "";
    }
}
