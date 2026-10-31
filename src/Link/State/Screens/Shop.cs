using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using SpireLink.Game;

namespace SpireLink.State.Screens;

// Group C. The "shop" field.
// Contract: {inventory_open, can_open_inventory, can_close_inventory, items: [card: {index, category: "card", price,
// is_stocked, can_afford, on_sale, card_id, card_name, card_type, card_cost, card_star_cost, card_rarity, card_description,
// keywords}, relic: {index, category: "relic", price, is_stocked, can_afford, relic_id, relic_name, relic_description,
// keywords}, potion: {index, category: "potion", price, is_stocked, can_afford, potion_id, potion_name, potion_description,
// keywords}, {index, category: "card_removal", price, is_stocked, can_afford}], can_proceed}; with a null inventory:
// items [], can_proceed and an error message.
// Items are indexed in inventory order: character cards, colorless cards, relics, potions, then card removal. A sold-out
// entry keeps its index and category but loses the item fields.
internal static class ShopScreen
{
    private const string NotReady =
        "Shop inventory is not ready yet (null). Often happens right after entering the merchant from the map; retry in a moment.";

    public static Dictionary<string, object?> Read(MerchantRoom room)
    {
        var ui = NMerchantRoom.Instance;
        bool open = ui?.Inventory?.IsOpen == true;
        var state = new Dictionary<string, object?>
        {
            ["inventory_open"] = open,
            ["can_open_inventory"] = !open && ui?.Inventory != null,
            ["can_close_inventory"] = open && Nodes.First<NBackButton>(ui) is { IsEnabled: true }
        };

        var items = new List<Dictionary<string, object?>>();
        var inventory = room.GetLocalInventory();
        if (inventory != null)
        {
            foreach (var entry in inventory.CardEntries) items.Add(Card(entry, items.Count));
            foreach (var entry in inventory.RelicEntries) items.Add(Relic(entry, items.Count));
            foreach (var entry in inventory.PotionEntries) items.Add(Potion(entry, items.Count));
            if (inventory.CardRemovalEntry is { } removal) items.Add(Item(removal, items.Count, "card_removal"));
        }
        state["items"] = items;
        state["can_proceed"] = ui?.ProceedButton?.IsEnabled ?? false;
        if (inventory == null) state["error"] = NotReady;
        return state;
    }

    // The fields every entry has; the price is the hook-modified one the merchant shows.
    private static Dictionary<string, object?> Item(MerchantEntry entry, int index, string category) => new()
    {
        ["index"] = index,
        ["category"] = category,
        ["price"] = entry.Cost,
        ["is_stocked"] = entry.IsStocked,
        ["can_afford"] = entry.EnoughGold
    };

    private static Dictionary<string, object?> Card(MerchantCardEntry entry, int index)
    {
        var item = Item(entry, index, "card");
        item["on_sale"] = entry.IsOnSale;
        if (entry.CreationResult?.Card is not { } card) return item;
        var info = Cards.Info(card);
        item["card_id"] = info["id"];
        item["card_name"] = info["name"];
        item["card_type"] = info["type"];
        item["card_cost"] = info["cost"];
        item["card_star_cost"] = info["star_cost"];
        item["card_rarity"] = info["rarity"];
        item["card_description"] = info["description"];
        item["keywords"] = info["keywords"];
        return item;
    }

    private static Dictionary<string, object?> Relic(MerchantRelicEntry entry, int index)
    {
        var item = Item(entry, index, "relic");
        if (entry.Model is not { } relic) return item;
        item["relic_id"] = relic.Id.Entry;
        item["relic_name"] = Text.Of(() => relic.Title);
        item["relic_description"] = Text.Of(() => relic.DynamicDescription);
        item["keywords"] = Tips.Of(relic.HoverTipsExcludingRelic);
        return item;
    }

    private static Dictionary<string, object?> Potion(MerchantPotionEntry entry, int index)
    {
        var item = Item(entry, index, "potion");
        if (entry.Model is not { } potion) return item;
        item["potion_id"] = potion.Id.Entry;
        item["potion_name"] = Text.Of(() => potion.Title);
        item["potion_description"] = Text.Of(() => potion.DynamicDescription);
        item["keywords"] = Tips.Of(potion.ExtraHoverTips);
        return item;
    }
}
