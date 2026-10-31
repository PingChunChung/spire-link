using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.HoverTips;

namespace SpireLink.Game;

// Hover tips as the "keywords" list: [{"name", "description"}], duplicates removed, a tip that cannot be read left out.
internal static class Tips
{
    public static List<Dictionary<string, object?>> Of(IEnumerable<IHoverTip>? tips, PileType pile = PileType.Hand)
    {
        var result = new List<Dictionary<string, object?>>();
        if (tips == null) return result;
        var seen = new HashSet<string>();
        try
        {
            foreach (var tip in IHoverTip.RemoveDupes(tips))
            {
                string? name, description;
                try
                {
                    switch (tip)
                    {
                        case HoverTip plain:
                            name = plain.Title == null ? null : Text.Plain(plain.Title);
                            description = Text.Plain(plain.Description);
                            break;
                        case CardHoverTip card:
                            name = Text.Of(() => card.Card.Title);
                            description = Cards.Description(card.Card, pile);
                            break;
                        default: continue;
                    }
                }
                catch { continue; }
                if (name == null && description == null) continue;
                if (!seen.Add(name ?? description!)) continue;
                result.Add(new() { ["name"] = name, ["description"] = description });
            }
        }
        catch { /* the tips read so far */ }
        return result;
    }
}
