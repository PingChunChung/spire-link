using Godot;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SpireLink.Game;
using SpireLink.State.Screens;

namespace SpireLink.Actions;

// Group F: map, event, rest site, shop, rewards, card reward, proceed, treasure and Crystal Sphere.
// Every handler clicks the same control the player would (ForceClick, the holder's Pressed signal, the map screen's own
// selection entry point), so the game's action queue and synchronizers do the rest; the body only says what was clicked.
// Indexes are the positions the matching State.Screens reader reports, so a list here is built the same way as there.
internal static class RoomActions
{
    public static Dictionary<string, object?> ChooseMapNode(ActionContext ctx)
    {
        if (!Ui.MapOpen) return Outcome.Fail("Map screen is not open");
        if (ctx.Int("index") is not { } index) return Outcome.Fail("Missing 'index' (map node index from next_options)");

        var screen = NMapScreen.Instance!;
        var options = MapScreen.Travelable();
        if (options.Count == 0) return Outcome.Fail("No travelable map nodes available");
        if (index < 0 || index >= options.Count)
            return Outcome.Fail($"Map node index {index} out of range ({options.Count} options available)");

        var target = options[index];
        var point = target.Point!;
        screen.OnMapPointSelectedLocally(target);
        return Outcome.Ok($"Traveling to {point.PointType} at ({point.coord.col},{point.coord.row})");
    }

    public static Dictionary<string, object?> ChooseEventOption(ActionContext ctx)
    {
        var ui = NEventRoom.Instance;
        if (ui == null) return ChooseModelOption(ctx);

        if (ctx.Int("index") is not { } index) return Outcome.Fail("Missing 'index' (event option index)");
        var buttons = Nodes.All<NEventOptionButton>(ui).ToList();
        if (buttons.Count == 0) return Outcome.Fail("No event options available");
        if (index < 0 || index >= buttons.Count)
            return Outcome.Fail($"Event option index {index} out of range ({buttons.Count} options)");

        var button = buttons[index];
        if (EventScreen.DialogueHitbox(Nodes.First<NAncientEventLayout>(ui)) != null)
            return Outcome.Fail("Ancient dialogue is active; advance it before choosing an option");
        if (button.Option.IsLocked) return Outcome.Fail($"Event option {index} is locked");
        if (!Ui.Enabled(button)) return Outcome.Fail($"Event option {index} is not actionable");

        var title = Text.Of(() => button.Option.Title) ?? "option";
        button.ForceClick();
        return Outcome.Ok($"Choosing event option: {title}");
    }

    // Events with a combat layout (Punch Off, ...) show no event room: the model's own options are answered through the
    // event synchronizer, by their index in CurrentOptions.
    private static Dictionary<string, object?> ChooseModelOption(ActionContext ctx)
    {
        if (ctx.Int("index") is not { } index) return Outcome.Fail("Missing 'index' (event option index)");
        if (ctx.Run.CurrentRoom is not EventRoom room) return Outcome.Fail("Event room is not open");
        var options = room.LocalMutableEvent.CurrentOptions;
        if (index < 0 || index >= options.Count)
            return Outcome.Fail($"Event option index {index} out of range ({options.Count} options)");
        if (options[index].IsLocked) return Outcome.Fail($"Event option {index} is locked");

        var title = Text.Of(() => options[index].Title) ?? "option";
        RunManager.Instance.EventSynchronizer.ChooseLocalOption(index);
        return Outcome.Ok($"Choosing event option: {title}");
    }

    public static Dictionary<string, object?> AdvanceDialogue(ActionContext ctx)
    {
        var ui = NEventRoom.Instance;
        if (ui == null) return Outcome.Fail("Event room is not open");
        var layout = Nodes.First<NAncientEventLayout>(ui);
        if (layout == null) return Outcome.Fail("No ancient dialogue active");
        var hitbox = EventScreen.DialogueHitbox(layout);
        if (hitbox == null) return Outcome.Fail("Dialogue hitbox not available - dialogue may have ended");

        hitbox.ForceClick();
        return Outcome.Ok("Advancing dialogue");
    }

    public static Dictionary<string, object?> ChooseRestOption(ActionContext ctx)
    {
        if (ctx.Int("index") is not { } index) return Outcome.Fail("Missing 'index' (rest site option index)");
        var room = NRestSiteRoom.Instance;
        if (room == null) return Outcome.Fail("Rest site room is not open");

        var buttons = Nodes.All<NRestSiteButton>(room).ToList();
        if (buttons.Count == 0) return Outcome.Fail("No rest site options available");
        if (index < 0 || index >= buttons.Count)
            return Outcome.Fail($"Rest option index {index} out of range ({buttons.Count} options)");

        var option = buttons[index].Option;
        if (!option.IsEnabled) return Outcome.Fail($"Rest option {index} ({option.OptionId}) is disabled");

        var name = Text.Of(() => option.Title) ?? option.OptionId;
        buttons[index].ForceClick();
        return Outcome.Ok($"Selecting rest site option: {name}");
    }

    // Buys entry `index` of the local inventory (AllEntries: cards, relics, potions, card removal, the order the shop
    // reader lists them). The inventory must be open first (open_shop) so the purchase is an audited two-step action.
    public static Dictionary<string, object?> ShopPurchase(ActionContext ctx)
    {
        MerchantInventory? inventory;
        switch (ctx.Run.CurrentRoom)
        {
            case MerchantRoom merchant:
                if (NMerchantRoom.Instance?.Inventory?.IsOpen != true) return Outcome.Fail("Shop inventory is closed; use open_shop first");
                inventory = merchant.GetLocalInventory();
                break;
            case EventRoom { CanonicalEvent: FakeMerchant } room when Events.Current(room) is FakeMerchant fake:
                // Before the fight the fake merchant's own inventory panel must be open; after it there is nothing to buy
                // and the inventory decides.
                if (!fake.StartedFight && FakeMerchantInventory() is { IsOpen: false })
                    return Outcome.Fail("Shop inventory is closed; use open_shop first");
                inventory = fake.Inventory;
                break;
            default:
                return Outcome.Fail("Not in a shop");
        }
        if (inventory == null) return Outcome.Fail("Shop inventory not ready yet; wait a moment and retry");
        if (ctx.Int("index") is not { } index) return Outcome.Fail("Missing 'index' (shop item index)");

        var entries = inventory.AllEntries.ToList();
        if (index < 0 || index >= entries.Count)
            return Outcome.Fail($"Shop item index {index} out of range ({entries.Count} items)");
        var entry = entries[index];
        if (!entry.IsStocked) return Outcome.Fail("Item is sold out");
        if (!entry.EnoughGold) return Outcome.Fail($"Not enough gold (need {entry.Cost}, have {ctx.Player.Gold})");

        // The purchase runs on the game's own task; the next observation shows its result.
        _ = entry.OnTryPurchaseWrapper(inventory);
        return Outcome.Ok($"Purchasing item for {entry.Cost} gold");
    }

    private static NFakeMerchant? FakeMerchantNode() => Nodes.First<NFakeMerchant>(NEventRoom.Instance);

    private static NMerchantInventory? FakeMerchantInventory() => Nodes.First<NMerchantInventory>(FakeMerchantNode());

    public static Dictionary<string, object?> ClaimReward(ActionContext ctx)
    {
        if (Overlay<NRewardsScreen>() is not { } screen) return Outcome.Fail("Rewards screen is not open");
        if (ctx.Int("index") is not { } index) return Outcome.Fail("Missing 'index' (reward index)");

        var buttons = RewardsScreen.Claimable(screen);
        if (index < 0 || index >= buttons.Count)
            return Outcome.Fail($"Reward index {index} out of range (screen has {buttons.Count} claimable rewards)");

        var button = buttons[index];
        var reward = button.Reward!;
        // A potion reward's button stays enabled with every slot full (procurement returns TooFull): refuse before the
        // click so a no-op never becomes an accepted receipt.
        if (reward is PotionReward potion && potion.Player.PotionSlots.All(slot => slot != null))
            return Outcome.Fail("Potion slots are full; discard a potion or proceed before claiming this reward");

        var description = reward switch
        {
            GoldReward gold => $"gold ({gold.Amount})",
            PotionReward p => $"potion ({Text.Of(() => p.Potion?.Title)})",
            CardReward => "card (opens card selection)",
            _ => RewardsScreen.TypeName(reward)
        };
        button.ForceClick();
        return Outcome.Ok($"Reward click dispatched: {description}; inspect the next observation for its effect");
    }

    public static Dictionary<string, object?> SelectCardReward(ActionContext ctx)
    {
        if (Overlay<NCardRewardSelectionScreen>() is not { } screen) return Outcome.Fail("Card reward selection screen is not open");
        if (ctx.Int("card_index") is not { } index) return Outcome.Fail("Missing 'card_index'");

        var holders = CardRewardScreen.Holders(screen);
        if (index < 0 || index >= holders.Count)
            return Outcome.Fail($"Card index {index} out of range (screen has {holders.Count} cards)");
        var holder = holders[index];
        if (!Ui.CardHolderEnabled(holder) || !CardRewardScreen.Pending(screen))
            return Outcome.Fail($"Card reward {index} is not actionable");

        var name = Text.Of(() => holder.CardModel?.Title) ?? "unknown";
        holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
        return Outcome.Ok($"Selecting card: {name}");
    }

    public static Dictionary<string, object?> SkipCardReward(ActionContext ctx)
        => CardRewardAlternative(CardRewardAlternatives.SkipOptionId, "skip", "Skipping card reward");

    public static Dictionary<string, object?> RerollCardReward(ActionContext ctx)
        => CardRewardAlternative(CardRewardAlternatives.RerollOptionId, "reroll", "Rerolling card reward");

    // Skip and reroll are alternative buttons bound by option id; the screen reader resolves the id to its button.
    private static Dictionary<string, object?> CardRewardAlternative(string optionId, string optionName, string message)
    {
        if (Overlay<NCardRewardSelectionScreen>() is not { } screen) return Outcome.Fail("Card reward selection screen is not open");
        var button = CardRewardScreen.Alternative(screen, optionId);
        if (button == null) return Outcome.Fail($"No {optionName} option available on this card reward");

        button.ForceClick();
        return Outcome.Ok(message);
    }

    // The proceed button of whichever screen has one enabled, tried in this order: the rewards overlay, the rest site,
    // the shop, the fake merchant, the treasure room. A shop or fake merchant with its inventory open refuses: close_shop
    // is its own audited action.
    public static Dictionary<string, object?> Proceed(ActionContext ctx)
    {
        if (Overlay<NRewardsScreen>() is { } rewards && Nodes.First<NProceedButton>(rewards) is { IsEnabled: true } fromRewards)
            return Click(fromRewards, "Proceeding from rewards");

        if (NRestSiteRoom.Instance is { ProceedButton: { IsEnabled: true } fromRest })
            return Click(fromRest, "Proceeding from rest site");

        if (NMerchantRoom.Instance is { } shop)
        {
            if (shop.Inventory?.IsOpen == true) return Outcome.Fail("Close the shop with close_shop before proceeding");
            if (shop.ProceedButton is { IsEnabled: true } fromShop) return Click(fromShop, "Proceeding from shop");
        }

        if (FakeMerchantNode() is { } fakeMerchant)
        {
            if (Nodes.First<NMerchantInventory>(fakeMerchant) is { IsOpen: true })
                return Outcome.Fail("Close the shop with close_shop before proceeding");
            if (Nodes.First<NProceedButton>(fakeMerchant) is { IsEnabled: true } fromFake)
                return Click(fromFake, "Proceeding from fake merchant");
        }

        if (Nodes.First<NTreasureRoom>(Nodes.Root) is { ProceedButton: { IsEnabled: true } fromTreasure })
            return Click(fromTreasure, "Proceeding from treasure room");

        return Outcome.Fail("No proceed button available or enabled");
    }

    public static Dictionary<string, object?> ClaimTreasureRelic(ActionContext ctx)
    {
        var room = Nodes.First<NTreasureRoom>(Nodes.Root);
        if (room == null) return Outcome.Fail("Treasure room is not open");
        var collection = room.GetNodeOrNull<NTreasureRoomRelicCollection>("%RelicCollection");
        if (collection?.Visible != true) return Outcome.Fail("Relic collection is not visible - chest may not be opened yet");
        if (ctx.Int("index") is not { } index) return Outcome.Fail("Missing 'index' (relic index)");

        // Same holders as TreasureScreen lists (a holder without a relic model gets no index there), so index N is the
        // relic the state showed at N. The old code counted model-less holders here too.
        var holders = Nodes.All<NTreasureRoomRelicHolder>(collection).Where(h => h.IsEnabled && h.Visible && h.Relic?.Model != null).ToList();
        if (index < 0 || index >= holders.Count)
            return Outcome.Fail($"Relic index {index} out of range ({holders.Count} relics available)");

        var holder = holders[index];
        var name = Text.Of(() => holder.Relic?.Model?.Title) ?? "unknown";
        holder.ForceClick();
        return Outcome.Ok($"Claiming treasure relic: {name}");
    }

    public static Dictionary<string, object?> CrystalSphereSetTool(ActionContext ctx)
    {
        if (Overlay<NCrystalSphereScreen>() is not { } screen) return Outcome.Fail("Crystal Sphere screen is not open");
        if (ctx.String("tool") is not { } tool) return Outcome.Fail("Missing 'tool' (expected 'big' or 'small')");

        var button = tool switch
        {
            "big" => screen.GetNodeOrNull<NClickableControl>("%BigDivinationButton"),
            "small" => screen.GetNodeOrNull<NClickableControl>("%SmallDivinationButton"),
            _ => null
        };
        if (button == null) return Outcome.Fail($"Unknown Crystal Sphere tool: {tool}");
        if (!button.Visible || !button.IsEnabled) return Outcome.Fail($"Crystal Sphere tool '{tool}' is not available");

        return Click(button, $"Setting Crystal Sphere tool to {tool}");
    }

    public static Dictionary<string, object?> CrystalSphereClickCell(ActionContext ctx)
    {
        if (Overlay<NCrystalSphereScreen>() is not { } screen) return Outcome.Fail("Crystal Sphere screen is not open");
        if (ctx.Int("x") is not { } x) return Outcome.Fail("Missing 'x' (cell x-coordinate)");
        if (ctx.Int("y") is not { } y) return Outcome.Fail("Missing 'y' (cell y-coordinate)");

        var cell = Nodes.All<NCrystalSphereCell>(screen).FirstOrDefault(c => c.Entity.X == x && c.Entity.Y == y);
        if (cell == null) return Outcome.Fail($"Crystal Sphere cell ({x}, {y}) was not found");
        if (!cell.Entity.IsHidden || !cell.Visible) return Outcome.Fail($"Crystal Sphere cell ({x}, {y}) is not clickable");

        // The cell reveals on Released, not on a press.
        cell.EmitSignal(NClickableControl.SignalName.Released, cell);
        return Outcome.Ok($"Clicking Crystal Sphere cell ({x}, {y})");
    }

    public static Dictionary<string, object?> CrystalSphereProceed(ActionContext ctx)
    {
        if (Overlay<NCrystalSphereScreen>() is not { } screen) return Outcome.Fail("Crystal Sphere screen is not open");
        var button = CrystalSphereScreen.ProceedButton(screen);
        if (button == null) return Outcome.Fail("Crystal Sphere proceed button is not enabled");
        return Click(button, "Proceeding from Crystal Sphere");
    }

    // The topmost overlay when it is a T.
    private static T? Overlay<T>() where T : class => Ui.Overlay<T>();

    private static Dictionary<string, object?> Click(NClickableControl button, string message)
    {
        button.ForceClick();
        return Outcome.Ok(message);
    }
}
