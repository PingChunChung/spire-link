using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Localization;

namespace SpireLink.Game;

// Game text as plain strings: BBCode tags removed, an [img]res://.../x.png[/img] kept as [x.png].
// Localization can throw while a screen is being built, so a getter that fails reads as null.
internal static class Text
{
    public static string Plain(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length;)
        {
            if (text[i] != '[') { sb.Append(text[i++]); continue; }
            if (text.AsSpan(i).StartsWith("[img]") && text.IndexOf("[/img]", i + 5, StringComparison.Ordinal) is var close and >= 0)
            {
                var path = text.AsSpan(i + 5, close - i - 5);
                sb.Append('[').Append(path[(path.LastIndexOf('/') + 1)..]).Append(']');
                i = close + 6;
                continue;
            }
            int end = text.IndexOf(']', i);
            if (end < 0) { sb.Append(text[i++]); continue; }
            i = end + 1;
        }
        return sb.ToString();
    }

    public static string? Of(LocString? text)
    {
        try { return text == null ? null : Plain(text.GetFormattedText()); }
        catch { return null; }
    }

    // Any text-like value: a LocString is formatted, anything else is ToString'd; null when the getter throws.
    public static string? Of(Func<object?> getter)
    {
        try
        {
            return getter() switch
            {
                null => null,
                LocString loc => Plain(loc.GetFormattedText()),
                var value => value.ToString()
            };
        }
        catch { return null; }
    }

    public static string? OneLine(string? text) => text?.Replace("\n", " ");

    // The "text" property of a Label or RichTextLabel, tags removed; null when the node is absent or has no text.
    public static string? OfLabel(Node? label)
    {
        if (!Nodes.Live(label)) return null;
        var text = label!.Get("text");
        return text.VariantType == Variant.Type.Nil ? null : Plain(text.AsString());
    }

    // A menu option name from a button label: lower case, runs of non-alphanumerics become one underscore.
    public static string? OptionName(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var sb = new StringBuilder();
        foreach (var c in Plain(label).Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var name = sb.ToString().Trim('_');
        return name.Length == 0 ? null : name;
    }
}
