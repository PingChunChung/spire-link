using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpireLink;

/// <summary>Visible, action-specific evidence that an advertised action took effect.</summary>
public static class ActionPostconditions
{
    public static bool Reached(AgentAction action, AgentSnapshot before, AgentSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var b = before.State;
        var a = after.State;
        var oldType = before.StateType;
        var newType = after.StateType;
        if (before.StateHash == after.StateHash || b.ValueKind != JsonValueKind.Object || a.ValueKind != JsonValueKind.Object)
            return false;
        if (action.Action != "menu_select" && Changed(Part(b, "context"), Part(a, "context"), "run_id"))
            return false;

        bool Left(string type) => oldType == type && newType != type && newType != "unknown";
        bool ItemChanged(string section, string list, string argument, string indexField = "index")
        {
            if (!ArgInt(action, argument, out var index)) return false;
            var oldItem = Indexed(Part(b, section), list, index, indexField);
            if (!Present(oldItem)) return false;
            if (newType != oldType) return Left(oldType);
            var matching = Items(Part(a, section), list).Where(item => SameIdentity(oldItem, item)).ToArray();
            if (matching.Length == 0) return true;
            return matching.All(newItem =>
                (True(oldItem, "actionable") && !True(newItem, "actionable")) ||
                (True(oldItem, "is_enabled") && !True(newItem, "is_enabled")) ||
                (True(oldItem, "is_stocked") && !True(newItem, "is_stocked")) ||
                (!True(oldItem, "was_chosen") && True(newItem, "was_chosen")));
        }

        // Amethyst Aubergine adds a second "15 Gold" reward: after claiming one, the other still matches the claimed item's
        // identity, so look at how many identical items are left instead.
        bool IdenticalItemRemoved(string section, string list, string argument)
        {
            if (!ArgInt(action, argument, out var index) || newType != oldType) return false;
            var oldItem = Indexed(Part(b, section), list, index);
            if (!Present(oldItem)) return false;
            return Items(Part(b, section), list).Count(item => SameIdentity(oldItem, item)) >
                   Items(Part(a, section), list).Count(item => SameIdentity(oldItem, item));
        }

        switch (action.Action)
        {
            case "play_card":
                if (!Combat(oldType) || !ArgInt(action, "card_index", out var cardIndex)) return false;
                var oldHand = Part(b, "player");
                var card = Indexed(oldHand, "hand", cardIndex);
                if (!Present(card)) return false;
                var instance = ArgString(action, "card_instance_id") ?? String(card, "instance_id");
                if (instance != null && String(card, "instance_id") != instance) return false;
                if (newType == "card_select" || newType == "bundle_select" || newType == "hand_select") return true;
                // A fight that ends on the enemy phase (poison, Battleworn Dummy) can land on the event's result page.
                if (newType != oldType) return newType is "rewards" or "card_reward" or "map" or "game_over" ||
                    (newType == "event" && after.Version > before.Version &&
                     String(Part(before.State, "context"), "run_id") is { Length: > 0 } runId &&
                     runId == String(Part(after.State, "context"), "run_id"));
                var afterHand = Part(a, "player");
                if (instance != null && !Items(afterHand, "hand").Any(c => String(c, "instance_id") == instance)) return true;
                if (instance == null && Items(afterHand, "hand").Count() < Items(oldHand, "hand").Count() &&
                    Items(afterHand, "hand").Count(c => SameIdentity(card, c)) <
                    Items(oldHand, "hand").Count(c => SameIdentity(card, c))) return true;
                return CombatNumberChanged(b, a, action);
            case "end_turn":
                if (!Combat(oldType)) return false;
                if (newType == "hand_select") return WellLaidPlansContinuation(b, a) || NextRoundSelectorContinuation(before, after);
                if (newType == "card_select") return EndTurnIntoEnemyChoice(before, after);
                // A fight that ends on the enemy phase (poison, Battleworn Dummy) can land on the event's result page.
                if (newType != oldType) return newType is "rewards" or "card_reward" or "map" or "game_over" ||
                    (newType == "event" && after.Version > before.Version &&
                     String(Part(before.State, "context"), "run_id") is { Length: > 0 } runId &&
                     runId == String(Part(after.State, "context"), "run_id"));
                return Number(Part(a, "battle"), "round") > Number(Part(b, "battle"), "round") ||
                       Changed(Part(b, "battle"), Part(a, "battle"), "turn");
            case "use_potion":
            case "discard_potion":
                if (!ArgInt(action, "slot", out var slot)) return false;
                var oldPotion = Indexed(Part(b, "player"), "potions", slot, "slot");
                if (!Present(oldPotion)) return false;
                var newPotion = Indexed(Part(a, "player"), "potions", slot, "slot");
                return !Present(newPotion) || !SameIdentity(oldPotion, newPotion);
            case "choose_map_node":
                // Map progress can update while the room is still loading.
                return oldType == "map" && Left("map");
            case "advance_dialogue":
                return oldType == "event" && (Left("event") ||
                       (newType == "event" && (Changed(Part(b, "event"), Part(a, "event"), "body") ||
                       Changed(Part(b, "event"), Part(a, "event"), "dialogue_text") ||
                       Changed(Part(b, "event"), Part(a, "event"), "dialogue_revision") ||
                       Changed(Part(b, "event"), Part(a, "event"), "options") ||
                       (True(Part(b, "event"), "in_dialogue") && !True(Part(a, "event"), "in_dialogue")))));
            case "choose_event_option":
                if (oldType != "event" || !ArgInt(action, "index", out var optionIndex)) return false;
                var oldOption = Indexed(Part(b, "event"), "options", optionIndex);
                if (!Present(oldOption)) return false;
                if (Left("event")) return true;
                var newOption = Indexed(Part(a, "event"), "options", optionIndex);
                // Slippery Bridge's Hold On remains selectable after use. Its
                // repeated choice needs the cost, reroll and HP loss together.
                if (newType == "event" && optionIndex == 1 &&
                    String(Part(b, "event"), "event_id") == "SLIPPERY_BRIDGE")
                {
                    if (String(Part(a, "event"), "event_id") != "SLIPPERY_BRIDGE") return false;
                    var nextOptions = Items(Part(a, "event"), "options").ToArray();
                    if (nextOptions.Length != 1 || !True(nextOptions[0], "is_proceed"))
                        return SlipperyBridgeHoldOn(before, after);
                }
                // Ancient rewards can retain a null body while replacing their
                // choices with a single proceed option. The selected choice
                // disappearing is direct UI progression evidence in that event.
                if (newType == "event" && Changed(Part(b, "event"), Part(a, "event"), "options")
                    && !Items(Part(a, "event"), "options").Any(item => SameIdentity(oldOption, item))) return true;
                // Events that repeat the same choice (Tablet of Truth, Slippery Bridge style) keep the option
                // identity. A different body or option text on the same event is direct evidence the click landed.
                if (newType == "event" && !True(Part(b, "event"), "is_ancient") &&
                    String(Part(b, "event"), "event_id") is { Length: > 0 } eventId &&
                    eventId != "SLIPPERY_BRIDGE" && eventId == String(Part(a, "event"), "event_id") && after.Version > before.Version &&
                    Present(newOption) && True(newOption, "actionable") &&
                    (Changed(Part(b, "event"), Part(a, "event"), "body") ||
                     Changed(Part(b, "event"), Part(a, "event"), "options"))) return true;
                return newType == "event" && ((SameIdentity(oldOption, newOption) && True(newOption, "was_chosen")) ||
                       (!Present(newOption) && Changed(Part(b, "event"), Part(a, "event"), "body")) ||
                       (!SameIdentity(oldOption, newOption) && Changed(Part(b, "event"), Part(a, "event"), "body")));
            case "choose_rest_option":
                return oldType == "rest_site" && (ItemChanged("rest_site", "options", "index") ||
                       (newType == "rest_site" && !True(Part(b, "rest_site"), "can_proceed") &&
                        True(Part(a, "rest_site"), "can_proceed")));
            case "claim_reward": return oldType == "rewards" && (ItemChanged("rewards", "items", "index") || IdenticalItemRemoved("rewards", "items", "index"));
            case "select_card_reward": return oldType == "card_reward" && ItemChanged("card_reward", "cards", "card_index");
            case "reroll_card_reward": return oldType == "card_reward" && (Left("card_reward") || Changed(Part(b, "card_reward"), Part(a, "card_reward"), "cards"));
            case "skip_card_reward": return Left("card_reward") ||
                    (oldType == "card_reward" && newType == "card_reward" && True(Part(b, "card_reward"), "can_skip") && !True(Part(a, "card_reward"), "can_skip"));
            case "select_card":
                if (oldType != "card_select" || !ArgInt(action, "index", out var selectIndex)) return false;
                var oldSelect = Indexed(Part(b, "card_select"), "cards", selectIndex);
                if (!Present(oldSelect)) return false;
                if (Left("card_select")) return true;
                var oldSelection = Part(b, "card_select");
                var newSelection = Part(a, "card_select");
                var newSelect = Indexed(newSelection, "cards", selectIndex);
                // A pick that opens a second, different selection (a choose-one card that then asks for a card from a pile)
                // replaces the screen type inside the same run and combat context.
                if (newType == "card_select" && after.Version > before.Version &&
                    String(oldSelection, "screen_type") is { Length: > 0 } oldScreen &&
                    String(newSelection, "screen_type") is { Length: > 0 } newScreen && oldScreen != newScreen &&
                    String(Part(b, "context"), "run_id") is { Length: > 0 } selectRun &&
                    selectRun == String(Part(a, "context"), "run_id")) return true;
                return newType == "card_select" &&
                       ((!True(oldSelect, "selected") && True(newSelect, "selected")) ||
                        (!True(oldSelection, "preview_showing") && True(newSelection, "preview_showing") &&
                         Items(newSelection, "preview_cards").Any(preview => SameIdentity(oldSelect, preview))));
            case "confirm_selection": return Left("card_select");
            case "cancel_selection":
                return Left("card_select") || (oldType == "card_select" && newType == "card_select" &&
                       ((True(Part(b, "card_select"), "preview_showing") && !True(Part(a, "card_select"), "preview_showing")) ||
                        SelectedCount(Part(a, "card_select"), "cards") < SelectedCount(Part(b, "card_select"), "cards")));
            case "select_bundle":
                if (oldType != "bundle_select" || !ArgInt(action, "index", out var bundleIndex)) return false;
                var oldBundle = Indexed(Part(b, "bundle_select"), "bundles", bundleIndex);
                if (!Present(oldBundle)) return false;
                if (Left("bundle_select")) return true;
                var newBundle = Indexed(Part(a, "bundle_select"), "bundles", bundleIndex);
                return newType == "bundle_select" && !True(oldBundle, "selected") && True(newBundle, "selected");
            case "confirm_bundle_selection": return Left("bundle_select");
            case "cancel_bundle_selection":
                return Left("bundle_select") || (oldType == "bundle_select" && newType == "bundle_select" &&
                       True(Part(b, "bundle_select"), "preview_showing") && !True(Part(a, "bundle_select"), "preview_showing"));
            case "combat_select_card":
                if (oldType != "hand_select" || !ArgInt(action, "card_index", out var handIndex)) return false;
                var selectedHandCard = Indexed(Part(b, "hand_select"), "cards", handIndex);
                if (!Present(selectedHandCard)) return false;
                if (Left("hand_select")) return true;
                // Upgrade selection moves to a "confirm card to upgrade" step: the other hand cards disappear and
                // the confirm button appears, without a selected_cards entry.
                if (newType == "hand_select" && !True(Part(b, "hand_select"), "can_confirm") &&
                    True(Part(a, "hand_select"), "can_confirm") &&
                    Items(Part(a, "hand_select"), "cards").Count() < Items(Part(b, "hand_select"), "cards").Count()) return true;
                return newType == "hand_select" &&
                       Items(Part(a, "hand_select"), "selected_cards").Count(c => SameIdentity(selectedHandCard, c)) >
                       Items(Part(b, "hand_select"), "selected_cards").Count(c => SameIdentity(selectedHandCard, c));
            case "combat_confirm_selection":
                return Left("hand_select") || SequentialDiscardConfirmation(before, after) || ConfirmIntoNextRoundSelector(before, after) ||
                       ConfirmIntoRepeatedSelector(before, after);
            case "select_relic": return oldType == "relic_select" && ItemChanged("relic_select", "relics", "index");
            case "skip_relic_selection": return Left("relic_select") ||
                    (oldType == "relic_select" && newType == "relic_select" && True(Part(b, "relic_select"), "can_skip") && !True(Part(a, "relic_select"), "can_skip"));
            case "open_shop": return ShopTransition(b, a, oldType, newType, false, true);
            case "close_shop": return ShopTransition(b, a, oldType, newType, true, false);
            case "shop_purchase":
                if (oldType is not ("shop" or "fake_merchant")) return false;
                if (!ArgInt(action, "index", out var shopIndex)) return false;
                var oldShop = Shop(b, oldType);
                var oldStock = Indexed(oldShop, "items", shopIndex);
                if (!Present(oldStock) || !True(oldStock, "is_stocked")) return false;
                if (String(oldStock, "category") == "card_removal" && newType == "card_select") return true;
                if (newType == "card_select" && PaidHammerContinuation(b, a, oldShop, oldStock)) return true;
                // Relics that open a card pick when bought (Dolly's Mirror, Royal Stamp, ...): the exact price leaving the
                // purse at a newer version, on the same floor, with a card selection that was not open before.
                if ((newType is "card_select" or "rewards" or "card_reward") && String(oldStock, "category") == "relic" && after.Version > before.Version &&
                    !Present(Part(b, "card_select")) && Number(oldStock, "price") is int relicPrice && relicPrice > 0 &&
                    Number(Part(b, "player"), "gold") is int relicGoldBefore &&
                    Number(Part(a, "player"), "gold") is int relicGoldAfter && relicGoldAfter == relicGoldBefore - relicPrice &&
                    Get(Part(b, "run"), "floor").GetRawText() == Get(Part(a, "run"), "floor").GetRawText()) return true;
                if (newType != oldType) return false;
                // Relics can stay listed as stocked for a while after purchase; gold dropping by exactly the
                // price at a newer version is direct evidence the purchase went through.
                if (after.Version > before.Version && Number(oldStock, "price") is int price && price > 0 &&
                    Number(Part(b, "player"), "gold") is int goldBefore &&
                    Number(Part(a, "player"), "gold") is int goldAfter && goldAfter == goldBefore - price) return true;
                var newStock = Indexed(Shop(a, newType), "items", shopIndex);
                return !Present(newStock) || !True(newStock, "is_stocked");
            case "open_chest":
                return oldType == "treasure" && newType == "treasure" &&
                       True(Part(b, "treasure"), "can_open_chest") && !True(Part(a, "treasure"), "can_open_chest") &&
                       (Items(Part(a, "treasure"), "relics").Any() || True(Part(a, "treasure"), "can_proceed"));
            case "claim_treasure_relic":
                return oldType == "treasure" && ItemChanged("treasure", "relics", "index");
            case "proceed":
                return (oldType is "rest_site" or "rewards" or "shop" or "fake_merchant" or "treasure") && Left(oldType);
            case "menu_select": return MenuReached(action, b, a, oldType, newType);
            case "crystal_sphere_set_tool":
                var tool = ArgString(action, "tool");
                return oldType == "crystal_sphere" && newType == "crystal_sphere" && tool != null &&
                       String(Part(b, "crystal_sphere"), "tool") != tool && String(Part(a, "crystal_sphere"), "tool") == tool;
            case "crystal_sphere_click_cell":
                if (oldType != "crystal_sphere" || !ArgInt(action, "x", out var x) || !ArgInt(action, "y", out var y)) return false;
                // The last divination can end the mini game and move on to the rewards in the same step.
                if (newType != "crystal_sphere")
                    return Left("crystal_sphere") && Present(Cell(Part(b, "crystal_sphere"), x, y)) &&
                           True(Cell(Part(b, "crystal_sphere"), x, y), "is_hidden") && after.Version > before.Version;
                var oldCell = Cell(Part(b, "crystal_sphere"), x, y);
                var newCell = Cell(Part(a, "crystal_sphere"), x, y);
                return Present(oldCell) && Present(newCell) && True(oldCell, "is_hidden") && !True(newCell, "is_hidden");
            case "crystal_sphere_proceed": return Left("crystal_sphere");
            default: return false;
        }
    }

    private static bool Combat(string type) => type is "monster" or "elite" or "boss";
    private static bool SlipperyBridgeHoldOn(AgentSnapshot before, AgentSnapshot after)
    {
        if (!before.Ready || !after.Ready || after.Version <= before.Version ||
            !before.Actions.Any(x => x.Action == "choose_event_option" &&
                x.Arguments.TryGetValue("index", out var i) && i is int index && index == 1) ||
            !after.Actions.Any(x => x.Action == "choose_event_option" &&
                x.Arguments.TryGetValue("index", out var i) && i is int index && index == 1)) return false;
        var b = before.State;
        var a = after.State;
        foreach (var key in new[] { "session_id", "run_id" })
        {
            var value = String(Part(b, "context"), key);
            if (string.IsNullOrWhiteSpace(value) || value != String(Part(a, "context"), key)) return false;
        }
        foreach (var key in new[] { "seed", "act", "floor" })
        {
            var oldValue = Get(Part(b, "run"), key);
            var newValue = Get(Part(a, "run"), key);
            if (!Present(oldValue) || !Present(newValue) || oldValue.GetRawText() != newValue.GetRawText()) return false;
        }
        if (!True(Part(b, "engine"), "action_queue_idle") ||
            !True(Part(a, "engine"), "action_queue_idle")) return false;
        var oldEvent = Part(b, "event");
        var newEvent = Part(a, "event");
        if (Get(oldEvent, "is_ancient").ValueKind != JsonValueKind.False ||
            Get(newEvent, "is_ancient").ValueKind != JsonValueKind.False ||
            Get(oldEvent, "in_dialogue").ValueKind != JsonValueKind.False ||
            Get(newEvent, "in_dialogue").ValueKind != JsonValueKind.False) return false;
        var oldOptions = Items(oldEvent, "options").ToArray();
        var newOptions = Items(newEvent, "options").ToArray();
        if (oldOptions.Length != 2 || newOptions.Length != 2) return false;
        var oldCard = Indexed(oldEvent, "options", 0);
        var newCard = Indexed(newEvent, "options", 0);
        var oldHold = Indexed(oldEvent, "options", 1);
        var newHold = Indexed(newEvent, "options", 1);
        if (Number(oldCard, "index") != 0 || Number(newCard, "index") != 0 ||
            Number(oldHold, "index") != 1 || Number(newHold, "index") != 1 ||
            !True(oldCard, "actionable") || !True(newCard, "actionable") ||
            !True(oldHold, "actionable") || !True(newHold, "actionable") ||
            string.IsNullOrWhiteSpace(String(oldCard, "title")) ||
            String(oldCard, "title") != String(newCard, "title") ||
            string.IsNullOrWhiteSpace(String(oldHold, "title")) ||
            Get(oldCard, "is_locked").ValueKind != JsonValueKind.False ||
            Get(newCard, "is_locked").ValueKind != JsonValueKind.False ||
            Get(oldHold, "is_locked").ValueKind != JsonValueKind.False ||
            Get(newHold, "is_locked").ValueKind != JsonValueKind.False ||
            Get(oldCard, "is_proceed").ValueKind != JsonValueKind.False ||
            Get(newCard, "is_proceed").ValueKind != JsonValueKind.False ||
            Get(oldHold, "is_proceed").ValueKind != JsonValueKind.False ||
            Get(newHold, "is_proceed").ValueKind != JsonValueKind.False ||
            Get(oldCard, "was_chosen").ValueKind != JsonValueKind.False ||
            Get(newCard, "was_chosen").ValueKind != JsonValueKind.False ||
            Get(oldHold, "was_chosen").ValueKind != JsonValueKind.False ||
            Get(newHold, "was_chosen").ValueKind != JsonValueKind.False ||
            !SameIdentity(oldHold, newHold)) return false;

        // HoldOn damages for CurrentHpLoss, increments the loop count and the
        // next displayed loss, then rerolls. The reroll and LOOP body may repeat.
        var oldText = String(oldHold, "description");
        var newText = String(newHold, "description");
        if (oldText == null || newText == null) return false;
        var templates = new[]
        {
            @"\ALose (?<cost>[1-9][0-9]*) HP\. The card in the above option is randomized\.\z",
            @"\A失去(?<cost>[1-9][0-9]*)点生命。重新随机上方选项中的卡牌。\z"
        };
        var matches = templates.Select(pattern => (Old: Regex.Match(oldText, pattern, RegexOptions.CultureInvariant),
            New: Regex.Match(newText, pattern, RegexOptions.CultureInvariant)))
            .FirstOrDefault(pair => pair.Old.Success && pair.New.Success);
        if (matches.Old == null || !matches.Old.Success ||
            !int.TryParse(matches.Old.Groups["cost"].Value, out var cost) || cost == int.MaxValue ||
            !int.TryParse(matches.New.Groups["cost"].Value, out var nextCost) || nextCost != cost + 1 ||
            Number(Part(b, "player"), "hp") is not int oldHp || oldHp < cost ||
            Number(Part(a, "player"), "hp") != oldHp - cost) return false;
        return true;
    }
    private static bool NextRoundSelectorContinuation(AgentSnapshot before, AgentSnapshot after)
    {
        // The next player round proves the submitted turn ended. A fresh, usable
        // selector is the observable point where the engine has stopped again.
        if (!after.Ready || after.Version <= before.Version ||
            before.StateType is not ("monster" or "elite" or "boss") ||
            after.StateType != "hand_select") return false;
        var b = before.State;
        var a = after.State;
        foreach (var key in new[] { "session_id", "run_id", "combat_epoch" })
        {
            var value = String(Part(b, "context"), key);
            if (string.IsNullOrWhiteSpace(value) || value != String(Part(a, "context"), key)) return false;
        }
        foreach (var key in new[] { "seed", "act", "floor" })
        {
            var oldValue = Get(Part(b, "run"), key);
            var newValue = Get(Part(a, "run"), key);
            if (!Present(oldValue) || !Present(newValue) || oldValue.GetRawText() != newValue.GetRawText()) return false;
        }
        var oldBattle = Part(b, "battle");
        var newBattle = Part(a, "battle");
        if (Number(oldBattle, "round") is not int round || round < 1 || round == int.MaxValue ||
            Number(newBattle, "round") != round + 1 ||
            String(oldBattle, "turn") != "player" || String(newBattle, "turn") != "player" ||
            !True(oldBattle, "is_play_phase") || !True(oldBattle, "can_end_turn") ||
            Get(oldBattle, "actions_disabled").ValueKind != JsonValueKind.False ||
            !True(Part(b, "engine"), "action_queue_idle") ||
            Get(newBattle, "is_play_phase").ValueKind != JsonValueKind.False ||
            Get(newBattle, "can_end_turn").ValueKind != JsonValueKind.False ||
            !True(newBattle, "actions_disabled") ||
            Get(Part(a, "engine"), "action_queue_idle").ValueKind != JsonValueKind.False ||
            Present(Part(b, "hand_select")) || Present(Part(a, "ui_overlay"))) return false;

        var selection = Part(a, "hand_select");
        var selector = Part(selection, "selector");
        var invocation = String(selector, "invocation_id");
        var invocationPrefix = String(Part(a, "context"), "session_id") + ":selection-";
        if (string.IsNullOrWhiteSpace(invocation) ||
            !invocation.StartsWith(invocationPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(invocation[invocationPrefix.Length..], "N", out _) ||
            invocation == String(Part(Part(b, "hand_select"), "selector"), "invocation_id") ||
            String(selection, "mode") != "simple_select" ||
            Get(selection, "can_confirm").ValueKind != JsonValueKind.False ||
            String(selector, "prompt_table") != "card_selection" ||
            String(selector, "prompt_key") != "TO_DISCARD" ||
            Number(selector, "min_select") != 1 || Number(selector, "max_select") != 1 ||
            Get(selector, "require_manual_confirmation").ValueKind != JsonValueKind.False ||
            Get(selector, "cancelable").ValueKind != JsonValueKind.False ||
            Get(selector, "filter_target_model_id").ValueKind != JsonValueKind.Null) return false;
        foreach (var key in new[] { "selected_cards", "selected_instances" })
        {
            var selected = Get(selection, key);
            if (selected.ValueKind != JsonValueKind.Undefined &&
                (selected.ValueKind != JsonValueKind.Array || selected.GetArrayLength() != 0)) return false;
        }
        var handValue = Get(Part(a, "player"), "hand");
        var cardsValue = Get(selection, "cards");
        if (handValue.ValueKind != JsonValueKind.Array || cardsValue.ValueKind != JsonValueKind.Array) return false;
        var hand = Items(Part(a, "player"), "hand").ToArray();
        var cards = Items(selection, "cards").ToArray();
        if (cards.Length == 0 || cards.Length != hand.Length ||
            cards.Any(c => !True(c, "actionable") ||
                Get(c, "selected").ValueKind != JsonValueKind.False || Number(c, "index") is not >= 0) ||
            cards.Select(c => Number(c, "index")).Distinct().Count() != cards.Length) return false;
        string Key(JsonElement card) => JsonSerializer.Serialize(new[] { String(card, "instance_id"),
            String(card, "id"), String(card, "type"), Get(card, "is_upgraded").GetRawText() });
        foreach (var set in new[] { hand, cards })
            if (set.Any(c => string.IsNullOrWhiteSpace(String(c, "instance_id")) ||
                string.IsNullOrWhiteSpace(String(c, "id")) ||
                String(c, "type") is not ("Attack" or "Skill" or "Power" or "Status" or "Curse") ||
                Get(c, "is_upgraded").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) ||
                set.Select(c => String(c, "instance_id")).Distinct(StringComparer.Ordinal).Count() != set.Length)
                return false;
        return hand.Select(Key).OrderBy(k => k, StringComparer.Ordinal)
            .SequenceEqual(cards.Select(Key).OrderBy(k => k, StringComparer.Ordinal)) &&
            cards.All(c => after.Actions.Any(action => action.Action == "combat_select_card" &&
                action.Arguments.TryGetValue("card_index", out var index) && index is int value && value == Number(c, "index")));
    }
    private static bool EndTurnIntoEnemyChoice(AgentSnapshot before, AgentSnapshot after)
    {
        // Some bosses open a card selection as their turn effect (Knowledge Demon, Vantom, The Insatiable).
        // The same combat context, a newer ready version and a selection screen show the click was consumed.
        if (!after.Ready || after.Version <= before.Version) return false;
        foreach (var key in new[] { "session_id", "run_id", "combat_epoch" })
        {
            var value = String(Part(before.State, "context"), key);
            if (string.IsNullOrWhiteSpace(value) || value != String(Part(after.State, "context"), key)) return false;
        }
        return Part(after.State, "card_select").ValueKind == JsonValueKind.Object;
    }

    private static bool ConfirmIntoNextRoundSelector(AgentSnapshot before, AgentSnapshot after)
    {
        // Confirming an end-of-turn selection (retain) can land directly on the next round's start-of-turn
        // selector (e.g. Tools of the Trade discard). A different invocation in the next player round proves progress.
        if (before.StateType != "hand_select" || after.StateType != "hand_select" || !after.Ready ||
            after.Version <= before.Version) return false;
        var b = before.State;
        var a = after.State;
        foreach (var key in new[] { "session_id", "run_id", "combat_epoch" })
        {
            var value = String(Part(b, "context"), key);
            if (string.IsNullOrWhiteSpace(value) || value != String(Part(a, "context"), key)) return false;
        }
        if (Number(Part(b, "battle"), "round") is not int round || Number(Part(a, "battle"), "round") != round + 1 ||
            String(Part(a, "battle"), "turn") != "player") return false;
        var oldSelection = Part(b, "hand_select");
        var newSelection = Part(a, "hand_select");
        var oldInvocation = String(Part(oldSelection, "selector"), "invocation_id");
        var newInvocation = String(Part(newSelection, "selector"), "invocation_id");
        return !string.IsNullOrWhiteSpace(oldInvocation) && !string.IsNullOrWhiteSpace(newInvocation) &&
               oldInvocation != newInvocation && True(oldSelection, "can_confirm");
    }

    private static bool ConfirmIntoRepeatedSelector(AgentSnapshot before, AgentSnapshot after)
    {
        // A card whose effect runs twice (Hand Trick under Burst) opens the same selector again right after the confirm.
        // The old invocation had a confirmable selection; the new one is a different invocation of the same prompt in the
        // same combat round with nothing selected yet: the confirm was consumed.
        if (before.StateType != "hand_select" || after.StateType != "hand_select" || !after.Ready ||
            after.Version <= before.Version) return false;
        var b = before.State;
        var a = after.State;
        foreach (var key in new[] { "session_id", "run_id", "combat_epoch" })
        {
            var value = String(Part(b, "context"), key);
            if (string.IsNullOrWhiteSpace(value) || value != String(Part(a, "context"), key)) return false;
        }
        if (Number(Part(b, "battle"), "round") is not int round || Number(Part(a, "battle"), "round") != round ||
            String(Part(b, "battle"), "turn") != "player" || String(Part(a, "battle"), "turn") != "player") return false;
        var oldSelection = Part(b, "hand_select");
        var newSelection = Part(a, "hand_select");
        var oldSelector = Part(oldSelection, "selector");
        var newSelector = Part(newSelection, "selector");
        var oldInvocation = String(oldSelector, "invocation_id");
        var newInvocation = String(newSelector, "invocation_id");
        return !string.IsNullOrWhiteSpace(oldInvocation) && !string.IsNullOrWhiteSpace(newInvocation) &&
               oldInvocation != newInvocation && True(oldSelection, "can_confirm") &&
               Items(oldSelection, "selected_cards").Count() == 1 &&
               Get(newSelection, "can_confirm").ValueKind == JsonValueKind.False &&
               !Items(newSelection, "selected_cards").Any() &&
               String(oldSelector, "prompt_key") is { Length: > 0 } promptKey && promptKey == String(newSelector, "prompt_key");
    }

    private static bool SequentialDiscardConfirmation(AgentSnapshot before, AgentSnapshot after)
    {
        // A selected card disappearing and a reset selection UI can both be seen
        // during the original selector's animation. Only a new invocation ID,
        // plus the selected physical card in the discard pile, proves progress.
        if (before.StateType != "hand_select" || after.StateType != "hand_select" || !after.Ready)
            return false;
        var b = before.State;
        var a = after.State;
        var oldContext = Part(b, "context");
        var newContext = Part(a, "context");
        foreach (var key in new[] { "session_id", "run_id", "combat_epoch" })
        {
            var value = String(oldContext, key);
            if (string.IsNullOrWhiteSpace(value) || value != String(newContext, key)) return false;
        }
        foreach (var key in new[] { "seed", "act", "floor" })
            if (!Present(Get(Part(b, "run"), key)) || !Present(Get(Part(a, "run"), key)) ||
                Get(Part(b, "run"), key).GetRawText() != Get(Part(a, "run"), key).GetRawText()) return false;
        if (Number(Part(b, "battle"), "round") is not int round ||
            Number(Part(a, "battle"), "round") != round ||
            String(Part(b, "battle"), "turn") != "player" ||
            String(Part(a, "battle"), "turn") != "player") return false;

        var oldSelection = Part(b, "hand_select");
        var newSelection = Part(a, "hand_select");
        var oldSelector = Part(oldSelection, "selector");
        var newSelector = Part(newSelection, "selector");
        bool DiscardSelector(JsonElement selector) =>
            String(selector, "prompt_table") == "card_selection" &&
            String(selector, "prompt_key") == "TO_DISCARD" &&
            Number(selector, "min_select") == 1 && Number(selector, "max_select") == 1 &&
            Get(selector, "require_manual_confirmation").ValueKind == JsonValueKind.False &&
            Get(selector, "cancelable").ValueKind == JsonValueKind.False &&
            Get(selector, "filter_target_model_id").ValueKind == JsonValueKind.Null;
        var oldInvocation = String(oldSelector, "invocation_id");
        var newInvocation = String(newSelector, "invocation_id");
        if (!DiscardSelector(oldSelector) || !DiscardSelector(newSelector) ||
            string.IsNullOrWhiteSpace(oldInvocation) || string.IsNullOrWhiteSpace(newInvocation) ||
            oldInvocation == newInvocation || String(oldSelection, "mode") != "simple_select" ||
            String(newSelection, "mode") != "simple_select" || !True(oldSelection, "can_confirm") ||
            Get(newSelection, "can_confirm").ValueKind != JsonValueKind.False) return false;

        var selected = Items(oldSelection, "selected_cards").ToArray();
        var selectedIds = Items(oldSelection, "selected_instances").ToArray();
        if (selected.Length != 1 || selectedIds.Length != 1 ||
            selectedIds[0].ValueKind != JsonValueKind.String ||
            !True(selected[0], "selected") ||
            String(selected[0], "instance_id") is not string instance ||
            string.IsNullOrWhiteSpace(instance) || selectedIds[0].GetString() != instance ||
            Items(newSelection, "selected_cards").Any() ||
            Items(newSelection, "selected_instances").Any() ||
            Get(newSelection, "selected_cards").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Array) ||
            Get(newSelection, "selected_instances").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Array)) return false;

        var oldHand = Items(Part(b, "player"), "hand").ToArray();
        var newHand = Items(Part(a, "player"), "hand").ToArray();
        var chosen = oldHand.Where(card => String(card, "instance_id") == instance).ToArray();
        if (chosen.Length != 1 || string.IsNullOrWhiteSpace(String(selected[0], "name")) ||
            String(chosen[0], "name") != String(selected[0], "name") ||
            string.IsNullOrWhiteSpace(String(chosen[0], "id")) ||
            newHand.Any(card => String(card, "instance_id") == instance)) return false;

        var oldDiscard = Items(Part(b, "player"), "discard_pile").ToArray();
        var newDiscard = Items(Part(a, "player"), "discard_pile").ToArray();
        bool IdentifiedPile(JsonElement[] cards) => cards.All(card =>
                !string.IsNullOrWhiteSpace(String(card, "id")) &&
                !string.IsNullOrWhiteSpace(String(card, "instance_id"))) &&
            cards.Select(card => String(card, "instance_id"))
                .Distinct(StringComparer.Ordinal).Count() == cards.Length;
        var newDraw = Items(Part(a, "player"), "draw_pile").ToArray();
        var newExhaust = Items(Part(a, "player"), "exhaust_pile").ToArray();
        if (!True(Part(b, "player"), "pile_identity_complete") ||
            !True(Part(a, "player"), "pile_identity_complete") ||
            Get(Part(b, "player"), "discard_pile").ValueKind != JsonValueKind.Array ||
            Get(Part(a, "player"), "discard_pile").ValueKind != JsonValueKind.Array ||
            Get(Part(a, "player"), "draw_pile").ValueKind != JsonValueKind.Array ||
            Get(Part(a, "player"), "exhaust_pile").ValueKind != JsonValueKind.Array ||
            Number(Part(b, "player"), "discard_pile_count") != oldDiscard.Length ||
            Number(Part(a, "player"), "discard_pile_count") != newDiscard.Length ||
            !IdentifiedPile(oldDiscard) || !IdentifiedPile(newDiscard) ||
            !IdentifiedPile(newDraw) || !IdentifiedPile(newExhaust) ||
            oldDiscard.Any(card => String(card, "instance_id") == instance) ||
            newDraw.Any(card => String(card, "instance_id") == instance) ||
            newExhaust.Any(card => String(card, "instance_id") == instance)) return false;
        bool landedInDiscard = newDiscard.Count(card => String(card, "instance_id") == instance &&
            String(card, "id") == String(chosen[0], "id")) == 1;
        // A discarded Sly card (Master Planner gives Skills Sly) is played for free on the spot: while its own
        // effect runs (Prepared: draw 1, discard 1) it sits in no pile, and that effect opens the next selector.
        // The chosen instance in no pile and no longer in hand, an unchanged discard pile and a card drawn into
        // the hand are the evidence of that nested play.
        bool playedOnDiscard = !landedInDiscard && newDiscard.Length == oldDiscard.Length &&
            !newDiscard.Any(card => String(card, "instance_id") == instance) &&
            newHand.Any(card => !oldHand.Any(old => String(old, "instance_id") == String(card, "instance_id")));
        if (!landedInDiscard && !playedOnDiscard) return false;

        var candidates = Items(newSelection, "cards").ToArray();
        if (candidates.Length == 0 || candidates.Length != newHand.Length ||
            !candidates.Any(card => True(card, "actionable")) ||
            candidates.Any(card => Get(card, "selected").ValueKind != JsonValueKind.False ||
                Get(card, "actionable").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                string.IsNullOrWhiteSpace(String(card, "instance_id")) ||
                string.IsNullOrWhiteSpace(String(card, "id"))) ||
            candidates.Select(card => String(card, "instance_id")).Distinct(StringComparer.Ordinal).Count() != candidates.Length ||
            newHand.Select(card => String(card, "instance_id")).Distinct(StringComparer.Ordinal).Count() != newHand.Length)
            return false;
        return candidates.All(card => newHand.Count(hand =>
            String(hand, "instance_id") == String(card, "instance_id") &&
            String(hand, "id") == String(card, "id")) == 1);
    }
    private static JsonElement Part(JsonElement obj, string name) => Get(obj, name);
    private static JsonElement Shop(JsonElement root, string type) => type == "shop" ? Part(root, "shop") : Part(Part(root, "fake_merchant"), "shop");
    private static JsonElement Get(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) ? value : default;
    private static bool Present(JsonElement item) => item.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
    private static bool True(JsonElement obj, string key) => Get(obj, key).ValueKind == JsonValueKind.True;
    private static string? String(JsonElement obj, string key) => Get(obj, key) is var v && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Number(JsonElement obj, string key) => Get(obj, key) is var v && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    private static IEnumerable<JsonElement> Items(JsonElement obj, string key) => Get(obj, key) is var v && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();
    private static JsonElement Indexed(JsonElement obj, string list, int index, string field = "index")
    {
        var position = 0;
        foreach (var item in Items(obj, list))
        {
            if ((Number(item, field) ?? position) == index) return item;
            position++;
        }
        return default;
    }
    private static JsonElement Cell(JsonElement sphere, int x, int y) => Items(sphere, "cells")
        .FirstOrDefault(c => Number(c, "x") == x && Number(c, "y") == y);
    private static bool Changed(JsonElement before, JsonElement after, string key)
    {
        var b = Get(before, key);
        var a = Get(after, key);
        return Present(b) && Present(a) && b.GetRawText() != a.GetRawText();
    }
    private static bool SameIdentity(JsonElement oldItem, JsonElement newItem)
    {
        if (!Present(oldItem) || !Present(newItem)) return false;
        foreach (var key in new[] { "instance_id", "id", "card_id", "relic_id", "potion_id", "name", "title", "description", "category", "type" })
        {
            var oldValue = Get(oldItem, key);
            if (!Present(oldValue)) continue;
            var newValue = Get(newItem, key);
            return Present(newValue) && oldValue.GetRawText() == newValue.GetRawText();
        }
        return false;
    }
    private static int SelectedCount(JsonElement selection, string list) => Items(selection, list).Count(c => True(c, "selected"));
    private static bool ArgInt(AgentAction action, string key, out int value)
    {
        value = 0;
        if (!action.Arguments.TryGetValue(key, out var raw)) return false;
        switch (raw)
        {
            case int i: value = i; return true;
            case long l when l >= int.MinValue && l <= int.MaxValue: value = (int)l; return true;
            case JsonElement e when e.ValueKind == JsonValueKind.Number: return e.TryGetInt32(out value);
            default: return false;
        }
    }
    private static string? ArgString(AgentAction action, string key) =>
        action.Arguments.TryGetValue(key, out var raw) ? raw is JsonElement e && e.ValueKind == JsonValueKind.String ? e.GetString() : raw as string : null;
    private static bool NumericChanged(JsonElement b, JsonElement a, string key) =>
        Number(b, key) is int oldValue && Number(a, key) is int newValue && oldValue != newValue;
    private static bool CombatNumberChanged(JsonElement before, JsonElement after, AgentAction action)
    {
        if (Number(Part(before, "battle"), "round") != Number(Part(after, "battle"), "round") ||
            Changed(Part(before, "battle"), Part(after, "battle"), "turn") ||
            Changed(Part(before, "context"), Part(after, "context"), "combat_epoch")) return false;
        var oldPlayer = Part(before, "player");
        var newPlayer = Part(after, "player");
        if (new[] { "energy", "hp", "block", "stars", "draw_pile_count", "discard_pile_count", "exhaust_pile_count" }
            .Any(key => NumericChanged(oldPlayer, newPlayer, key))) return true;
        var oldEnemies = Items(Part(before, "battle"), "enemies");
        var newEnemies = Items(Part(after, "battle"), "enemies");
        var target = ArgString(action, "target");
        if (target != null)
        {
            var oldEnemy = oldEnemies.FirstOrDefault(e => Get(e, "combat_id").ToString() == target);
            if (!Present(oldEnemy)) return false;
            var newEnemy = newEnemies.FirstOrDefault(e => Get(e, "combat_id").ToString() == target);
            return !Present(newEnemy) || new[] { "hp", "block" }.Any(key => NumericChanged(oldEnemy, newEnemy, key));
        }
        return oldEnemies.Any(oldEnemy =>
        {
            var id = Get(oldEnemy, "combat_id").ToString();
            var newEnemy = newEnemies.FirstOrDefault(e => Get(e, "combat_id").ToString() == id);
            return !Present(newEnemy) || new[] { "hp", "block" }.Any(key => NumericChanged(oldEnemy, newEnemy, key));
        });
    }
    private static bool ShopTransition(JsonElement b, JsonElement a, string oldType, string newType, bool from, bool to) =>
        (oldType is "shop" or "fake_merchant") && newType == oldType &&
        True(Shop(b, oldType), "inventory_open") == from && True(Shop(a, newType), "inventory_open") == to;
    private static bool WellLaidPlansContinuation(JsonElement b, JsonElement a)
    {
        // End-turn dispatch reached a mandatory child UI; the turn itself is not over.
        // Identify the live selector's localization key, filter owner and preferences;
        // formatted prompt text cannot establish the source of an arbitrary hand UI.
        foreach (var key in new[] { "run_id", "combat_epoch" })
        {
            var value = String(Part(b, "context"), key);
            if (string.IsNullOrWhiteSpace(value) || value != String(Part(a, "context"), key)) return false;
        }
        foreach (var key in new[] { "act", "floor", "ascension" })
            if (Number(Part(b, "run"), key) is not int value || value < (key == "act" ? 1 : 0) ||
                Number(Part(a, "run"), key) != value) return false;
        var seed = String(Part(b, "run"), "seed");
        if (string.IsNullOrWhiteSpace(seed) || seed != String(Part(a, "run"), "seed")) return false;
        var oldBattle = Part(b, "battle");
        var newBattle = Part(a, "battle");
        if (!True(oldBattle, "is_play_phase") || !True(oldBattle, "can_end_turn") ||
            Get(oldBattle, "actions_disabled").ValueKind != JsonValueKind.False ||
            Get(newBattle, "is_play_phase").ValueKind != JsonValueKind.False ||
            Get(newBattle, "can_end_turn").ValueKind != JsonValueKind.False || !True(newBattle, "actions_disabled") ||
            Number(oldBattle, "round") is not int round || round < 1 || Number(newBattle, "round") != round ||
            String(oldBattle, "turn") != "player" || String(newBattle, "turn") != "player" ||
            !True(Part(b, "engine"), "action_queue_idle") ||
            Get(Part(a, "engine"), "action_queue_idle").ValueKind != JsonValueKind.False ||
            Present(Part(b, "hand_select")) || Present(Part(a, "ui_overlay"))) return false;
        int? amount = null;
        foreach (var root in new[] { b, a })
        {
            var powers = Items(Part(root, "player"), "status").Where(p => String(p, "id") == "WELL_LAID_PLANS_POWER").ToArray();
            if (powers.Length != 1 || Number(powers[0], "amount") is not int observed || observed <= 0 ||
                (amount != null && amount != observed)) return false;
            amount = observed;
        }
        var selection = Part(a, "hand_select");
        var selector = Part(selection, "selector");
        if (String(selection, "mode") != "simple_select" || !True(selection, "can_confirm") ||
            String(selector, "prompt_table") != "powers" ||
            String(selector, "prompt_key") != "WELL_LAID_PLANS_POWER.selectionScreenPrompt" ||
            String(selector, "filter_target_model_id") != "WELL_LAID_PLANS_POWER" ||
            Number(selector, "min_select") != 0 || Number(selector, "max_select") != amount ||
            !True(selector, "require_manual_confirmation") || Get(selector, "cancelable").ValueKind != JsonValueKind.False) return false;
        foreach (var key in new[] { "selected_cards", "selected_instances" })
        {
            var selected = Get(selection, key);
            // The capture contract omits these fields when no cards are selected.
            if (selected.ValueKind != JsonValueKind.Undefined &&
                (selected.ValueKind != JsonValueKind.Array || selected.GetArrayLength() != 0)) return false;
        }
        var newHand = Items(Part(a, "player"), "hand").ToArray();
        var cards = Items(selection, "cards").ToArray();
        if (cards.Length == 0 || !cards.Any(c => True(c, "actionable")) ||
            cards.Any(c => Get(c, "actionable").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            Get(c, "selected").ValueKind != JsonValueKind.False || Number(c, "index") is not >= 0) ||
            cards.Select(c => Number(c, "index")).Distinct().Count() != cards.Length) return false;
        string Key(JsonElement card) => JsonSerializer.Serialize(new[] { String(card, "instance_id"), String(card, "id"),
            String(card, "type"), Get(card, "is_upgraded").GetRawText() });
        foreach (var hand in new[] { newHand, cards })
            if (hand.Any(c => string.IsNullOrWhiteSpace(String(c, "instance_id")) || string.IsNullOrWhiteSpace(String(c, "id")) ||
                String(c, "type") is not ("Attack" or "Skill" or "Power" or "Status" or "Curse") ||
                Get(c, "is_upgraded").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) ||
                hand.Select(c => String(c, "instance_id")).Distinct(StringComparer.Ordinal).Count() != hand.Length) return false;
        // The canonical selector proves this child belongs to turn-end retention.
        // Earlier turn-end hooks may exhaust, remove or add cards. Validate the current
        // hand against its UI and use actual filter booleans, not pre-turn hand contents.
        // The retention selector omits cards that already Retain (they are kept anyway), so the UI may list fewer
        // cards than the hand. Every UI card must still be a current hand card, and every hand card the UI leaves
        // out must carry the Retain keyword.
        var uiKeys = cards.Select(Key).ToHashSet(StringComparer.Ordinal);
        var handKeys = newHand.Select(Key).ToHashSet(StringComparer.Ordinal);
        if (!uiKeys.IsSubsetOf(handKeys)) return false;
        static bool Retains(JsonElement card) => Items(card, "kw").Any(k => k.ValueKind == JsonValueKind.String && k.GetString() == "Retain");
        return newHand.Where(c => !uiKeys.Contains(Key(c))).All(Retains);
    }
    private static bool PaidHammerContinuation(JsonElement b, JsonElement a, JsonElement oldShop, JsonElement stock)
    {
        // This acknowledges the paid purchase reaching its mandatory pickup UI.
        // It does not assert that the enchantment or the asynchronous pickup finished.
        if (String(stock, "category") != "relic" || String(stock, "relic_id") != "GNARLED_HAMMER" ||
            !True(oldShop, "inventory_open") || Present(Part(b, "card_select")) ||
            Number(stock, "price") is not int price || price <= 0 ||
            Number(Part(b, "player"), "gold") is not int oldGold ||
            Number(Part(a, "player"), "gold") is not int newGold ||
            oldGold < price || newGold < 0 || (long)oldGold - newGold != price) return false;
        var runId = String(Part(b, "context"), "run_id");
        if (string.IsNullOrWhiteSpace(runId) || String(Part(a, "context"), "run_id") != runId) return false;
        var oldRun = Part(b, "run");
        var newRun = Part(a, "run");
        if (new[] { "act", "floor", "ascension", "seed" }.Any(key =>
            !Present(Get(oldRun, key)) || !Present(Get(newRun, key)) ||
            Get(oldRun, key).GetRawText() != Get(newRun, key).GetRawText())) return false;
        var selection = Part(a, "card_select");
        var cards = Items(selection, "cards").ToArray();
        var deck = Items(Part(b, "player"), "deck").ToArray();
        // Unknown types may hide additional eligible Attacks; do not filter them out.
        if (deck.Any(c => String(c, "type") is not ("Attack" or "Skill" or "Power" or "Status" or "Curse"))) return false;
        var attacks = deck.Where(c => String(c, "type") == "Attack").ToArray();
        if (String(selection, "screen_type") != "NDeckEnchantSelectScreen" ||
            !True(selection, "can_confirm") || Get(selection, "preview_showing").ValueKind != JsonValueKind.False ||
            cards.Length == 0 || cards.Length != attacks.Length ||
            cards.Any(c => String(c, "type") != "Attack" || !True(c, "actionable") ||
                Get(c, "selected").ValueKind != JsonValueKind.False) ||
            cards.Concat(attacks).Any(c => string.IsNullOrWhiteSpace(String(c, "id")) ||
                Get(c, "is_upgraded").ValueKind is not (JsonValueKind.True or JsonValueKind.False))) return false;
        string Key(JsonElement card) => JsonSerializer.Serialize(new[] { String(card, "id"), Get(card, "is_upgraded").GetRawText() });
        return cards.Select(Key).OrderBy(k => k, StringComparer.Ordinal)
            .SequenceEqual(attacks.Select(Key).OrderBy(k => k, StringComparer.Ordinal));
    }
    private static bool TutorialRunStarted(AgentAction action, JsonElement b, JsonElement a, string newType)
    {
        var option = ArgString(action, "option");
        if (option is not ("yes" or "no") || String(b, "menu_screen") != "tutorial_prompt" ||
            String(Part(b, "ui_overlay"), "type") != "NAcceptTutorialsFtue" ||
            !Items(b, "options").Any(o => OptionName(o) == option && True(o, "enabled")) ||
            Part(b, "context").ValueKind != JsonValueKind.Object ||
            Get(Part(b, "context"), "run_id").ValueKind != JsonValueKind.Null || Present(Part(b, "run")) ||
            newType is not ("map" or "event") || Part(a, newType).ValueKind != JsonValueKind.Object ||
            string.IsNullOrWhiteSpace(String(Part(a, "context"), "run_id")) ||
            !True(Part(a, "engine"), "action_queue_idle") ||
            Present(Part(a, "ui_overlay"))) return false;
        var run = Part(a, "run");
        return Number(run, "act") == 1 && Number(run, "floor") == 0 && Number(run, "ascension") == 0 &&
            !string.IsNullOrWhiteSpace(String(run, "seed"));
    }
    private static bool MenuReached(AgentAction action, JsonElement b, JsonElement a, string oldType, string newType)
    {
        var option = ArgString(action, "option");
        if (option == null || oldType is not ("menu" or "game_over")) return false;
        if (oldType == "game_over") return option == "main_menu" && newType == "menu";
        if (newType != "menu" && String(b, "menu_screen") == "tutorial_prompt")
            return TutorialRunStarted(action, b, a, newType);
        if (newType != "menu") return (option is "continue" or "confirm" or "embark") &&
            newType is "map" or "event" or "monster" or "elite" or "boss" or "rest_site" or "shop"
                or "fake_merchant" or "treasure" or "rewards" or "card_reward" or "card_select"
                or "bundle_select" or "hand_select" or "relic_select" or "crystal_sphere" or "game_over";
        if (Changed(b, a, "menu_screen")) return true;
        if (Changed(b, a, "ui_overlay")) return true;
        if (option == String(a, "selected_character_id") && option != String(b, "selected_character_id")) return true;
        var oldOptions = Items(b, "options").Select(OptionName).ToArray();
        var newOptions = Items(a, "options").Select(OptionName).ToArray();
        return oldOptions.Contains(option) && !newOptions.Contains(option) && !oldOptions.SequenceEqual(newOptions);
    }
    private static string? OptionName(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : String(value, "name");
}
