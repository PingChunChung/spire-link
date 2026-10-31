using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;
using SpireLink.Game;

namespace SpireLink.State.Screens;

// Group B. The "fake_merchant" field (the "?" node merchant who sells fake relics).
// Contract: {event_id, event_name, started_fight, shop: {items: [{index, category: "relic", price, is_stocked, can_afford,
// relic_id?, relic_name?, relic_description?, keywords?, relic_error?}], can_proceed, inventory_open?, can_open_inventory?,
// can_close_inventory?, error?}, message?}. After the fight: shop {items: [], can_proceed: true} and a message.
internal static class FakeMerchantScreen
{
    public static Dictionary<string, object?> Read(EventRoom room)
    {
        var merchant = Events.Current(room) as FakeMerchant
            ?? throw new InvalidOperationException("the current event is not the fake merchant");
        var state = new Dictionary<string, object?>
        {
            ["event_id"] = merchant.Id.Entry,
            ["event_name"] = Text.Of(() => merchant.Title),
            ["started_fight"] = merchant.StartedFight
        };
        if (merchant.StartedFight)
        {
            state["shop"] = new Dictionary<string, object?> { ["items"] = new List<Dictionary<string, object?>>(), ["can_proceed"] = true };
            state["message"] = "The fake merchant has been defeated. Proceed to map.";
            return state;
        }

        var shop = Shop(merchant.Inventory);
        if (Nodes.First<NFakeMerchant>(NEventRoom.Instance) is { } node)
        {
            shop["can_proceed"] = Nodes.First<NProceedButton>(node)?.IsEnabled ?? false;
            bool open = Nodes.First<NMerchantInventory>(node)?.IsOpen == true;
            shop["inventory_open"] = open;
            shop["can_open_inventory"] = !open && node.MerchantButton is { IsEnabled: true, Visible: true };
            shop["can_close_inventory"] = open && Nodes.First<NBackButton>(node) is { IsEnabled: true };
        }
        else shop["can_proceed"] = false;
        state["shop"] = shop;
        return state;
    }

    // The relic entries (the fake merchant sells nothing else). The inventory is built when the player's copy of the event
    // is set up, so it can still be missing right after entering.
    private static Dictionary<string, object?> Shop(MerchantInventory? inventory)
    {
        var shop = new Dictionary<string, object?>();
        if (inventory == null)
        {
            shop["items"] = new List<Dictionary<string, object?>>();
            shop["error"] = "Fake merchant inventory is not ready yet; retry in a moment.";
            return shop;
        }
        shop["items"] = inventory.RelicEntries.Select(Item).ToList();
        return shop;
    }

    // The fake relics (fake_mango, fake_happy_flower, ...) are not ordinary relics: a field that cannot be read falls back
    // instead of failing the whole observation.
    private static Dictionary<string, object?> Item(MerchantRelicEntry entry, int index)
    {
        var item = new Dictionary<string, object?> { ["index"] = index, ["category"] = "relic" };
        try { item["price"] = entry.Cost; } catch { item["price"] = null; }
        try { item["is_stocked"] = entry.IsStocked; } catch { item["is_stocked"] = false; }
        try { item["can_afford"] = entry.EnoughGold; } catch { item["can_afford"] = false; }
        try
        {
            if (entry.Model is { } relic)
            {
                item["relic_id"] = relic.Id.Entry;
                item["relic_name"] = Text.Of(() => relic.Title);
                item["relic_description"] = Text.Of(() => relic.DynamicDescription);
                try { item["keywords"] = Tips.Of(relic.HoverTipsExcludingRelic); }
                catch { item["keywords"] = new List<Dictionary<string, object?>>(); }
            }
        }
        catch (Exception ex) { item["relic_error"] = ex.GetType().Name; }
        return item;
    }

    // The error body the observation keeps when the merchant cannot be described.
    public static Dictionary<string, object?>? ReadOrError(EventRoom room)
    {
        try { return Read(room); }
        catch (Exception ex)
        {
            return new()
            {
                ["shop"] = new Dictionary<string, object?> { ["items"] = new List<Dictionary<string, object?>>(), ["can_proceed"] = false },
                ["error"] = ex.GetType().Name + ": " + ex.Message
            };
        }
    }
}
