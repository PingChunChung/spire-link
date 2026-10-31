using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;

namespace SpireLink.Game;

// Card JSON: the shared card record (Info), the hand record (InHand), pile summaries and preview lists.
internal static class Cards
{
    public static string Cost(CardModel card) => card.EnergyCost.CostsX ? "X" : card.EnergyCost.GetAmountToSpend().ToString();

    public static string? StarCost(CardModel card)
        => card.HasStarCostX ? "X" : card.CurrentStarCost >= 0 ? card.GetStarCostWithModifiers().ToString() : null;

    // The description as shown in `pile`, on one line; the static description when the pile-specific one fails.
    public static string? Description(CardModel card, PileType pile = PileType.Hand)
    {
        try { return Text.OneLine(Text.Plain(card.GetDescriptionForPile(pile))); }
        catch { return Text.OneLine(Text.Of(() => card.Description)); }
    }

    // A throwaway upgraded copy for previews; null when the card cannot be upgraded or the copy fails.
    public static CardModel? Upgraded(CardModel card)
    {
        if (!card.IsUpgradable) return null;
        try
        {
            var copy = card.IsMutable ? (CardModel)card.MutableClone() : card.ToMutable();
            copy.UpgradeInternal();
            return copy;
        }
        catch { return null; }
    }

    public static string? UpgradedDescription(CardModel card, PileType pile = PileType.Hand)
    {
        if (Upgraded(card) is { } copy) return Description(copy, pile);
        return card.IsUpgradable ? Text.OneLine(Text.Of(card.GetDescriptionForUpgradePreview)) : null;
    }

    // {id, name, type, cost, star_cost, description, rarity, is_upgraded, keywords}: every card list starts from this.
    public static Dictionary<string, object?> Info(CardModel card, PileType pile = PileType.None) => new()
    {
        ["id"] = card.Id.Entry,
        ["name"] = Text.Of(() => card.Title),
        ["type"] = card.Type.ToString(),
        ["cost"] = Cost(card),
        ["star_cost"] = StarCost(card),
        ["description"] = Description(card, pile),
        ["rarity"] = card.Rarity.ToString(),
        ["is_upgraded"] = card.IsUpgraded,
        ["keywords"] = Tips.Of(card.HoverTips)
    };

    // Info plus the position and clickability a choice screen reports.
    public static Dictionary<string, object?> Choice(CardModel card, int index, bool actionable)
    {
        var info = Info(card);
        info["index"] = index;
        info["actionable"] = actionable;
        return info;
    }

    // A card in hand: Info with affliction, index, target type, playability, raw variables and keyword names.
    public static Dictionary<string, object?> InHand(CardModel card, int index)
    {
        card.CanPlay(out var unplayable, out _);
        var state = Info(card);
        try
        {
            if (card.Affliction is { } affliction)
                state["affliction"] = new Dictionary<string, object?> { ["id"] = affliction.Id.Entry, ["amount"] = affliction.Amount };
        }
        catch { }
        state["index"] = index;
        state["description"] = Description(card);
        state["target_type"] = card.TargetType.ToString();
        state["can_play"] = unplayable == UnplayableReason.None;
        state["unplayable_reason"] = unplayable == UnplayableReason.None ? null : unplayable.ToString();
        state["vars"] = Vars(card);
        state["kw"] = card.Keywords.Select(k => k.ToString()).ToArray();
        return state;
    }

    // Base and preview value of every dynamic variable; "enchanted" only when an enchantment changed it.
    public static Dictionary<string, object?>? Vars(CardModel card)
    {
        try
        {
            var vars = new Dictionary<string, object?>();
            foreach (var (name, value) in card.DynamicVars)
            {
                var entry = new Dictionary<string, object?> { ["base"] = (double)value.BaseValue, ["preview"] = (double)value.PreviewValue };
                if (value.EnchantedValue != value.BaseValue) entry["enchanted"] = (double)value.EnchantedValue;
                vars[name] = entry;
            }
            return vars;
        }
        catch { return null; }
    }

    // Draw, discard and exhaust piles: name, cost, star cost and the pile-specific description only.
    public static List<Dictionary<string, object?>> Pile(IEnumerable<CardModel> cards, PileType pile)
        => cards.Select(card => new Dictionary<string, object?>
        {
            ["name"] = Text.Of(() => card.Title),
            ["cost"] = Cost(card),
            ["star_cost"] = StarCost(card),
            ["description"] = Description(card, pile)
        }).ToList();

    // Appends the cards shown in a visible preview container (an upgrade or enchant preview), non-actionable, indexed on from
    // what the list already holds. Full holders are read in screen order; preview-only holders in tree order.
    public static void AddPreviews(Control? container, List<Dictionary<string, object?>> into)
    {
        if (!Nodes.Visible(container)) return;
        var holders = Nodes.ByPosition(Nodes.All<NCardHolder>(container));
        var cards = holders.Count > 0
            ? holders.Select(h => h.CardModel)
            : Nodes.All<NPreviewCardHolder>(container).Select(h => h.CardModel);
        foreach (var card in cards)
            if (card != null) into.Add(Choice(card, into.Count, false));
    }
}
