using MegaCrit.Sts2.Core.Models;

namespace SpireLink.Game;

// Relic records shared by the screens that offer a relic to pick (treasure room, choose-a-relic).
internal static class Relics
{
    // {index, id, name, description, rarity, keywords}; the screen adds "actionable" when it reports one.
    public static Dictionary<string, object?> Choice(RelicModel relic, int index) => new()
    {
        ["index"] = index,
        ["id"] = relic.Id.Entry,
        ["name"] = Text.Of(() => relic.Title),
        ["description"] = Text.Of(() => relic.DynamicDescription),
        ["rarity"] = relic.Rarity.ToString(),
        ["keywords"] = Tips.Of(relic.HoverTipsExcludingRelic)
    };
}
