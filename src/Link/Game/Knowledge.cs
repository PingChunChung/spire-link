using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace SpireLink.Game;

// Card rows for /v1/knowledge/cards: a card as a template, outside any run.
internal static class Knowledge
{
    // One variant ("base" or "upgraded") of a card: {variant, id, name, type, rarity, cost, star_cost, description,
    // is_upgraded, is_upgradable, current_upgrade_level, max_upgrade_level, keywords: [{name, description}]}.
    // The pile picks the description text; the catalog passes None so the row does not depend on the room it was built in.
    public static Dictionary<string, object?> CardVariant(CardModel card, string variant, PileType pile = PileType.Hand) => new()
    {
        ["variant"] = variant,
        ["id"] = Text.Of(() => card.Id.Entry),
        ["name"] = Text.Of(() => card.Title),
        ["type"] = Text.Of(() => card.Type),
        ["rarity"] = Text.Of(() => card.Rarity),
        ["cost"] = Cards.Cost(card),
        ["star_cost"] = Cards.StarCost(card),
        ["description"] = Cards.Description(card, pile),
        ["is_upgraded"] = card.IsUpgraded,
        ["is_upgradable"] = card.IsUpgradable,
        ["current_upgrade_level"] = card.CurrentUpgradeLevel,
        ["max_upgrade_level"] = card.MaxUpgradeLevel,
        ["keywords"] = Tips.Of(card.HoverTips, pile)
    };
}
