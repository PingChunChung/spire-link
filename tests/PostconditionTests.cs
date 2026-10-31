using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpireLink;

static class PostconditionTests
{
    static int checks;
    static AgentSnapshot Snap(string json, long version = 1, bool ready = true)
    {
        using var document = JsonDocument.Parse(json);
        var state = document.RootElement;
        return new AgentSnapshot(version, json, state, AgentProtocol.BuildActions(state, ready), ready,
            state.GetProperty("state_type").GetString()!, DateTimeOffset.UtcNow);
    }
    static AgentAction Action(string name, params (string Key, object Value)[] args) =>
        new("test", name, args.ToDictionary(x => x.Key, x => (object?)x.Value), name);
    static void Check(bool expected, AgentAction action, string before, string after, string reason)
    {
        var result = ActionPostconditions.Reached(action, Snap(before), Snap(after));
        if (result != expected) throw new Exception($"{reason}: expected {expected}, got {result}");
        checks++;
    }
    static string Rewrite(string json, System.Action<JsonNode> edit)
    {
        var node = JsonNode.Parse(json)!;
        edit(node);
        return node.ToJsonString();
    }
    static JsonObject RetentionSelector(int amount) => new()
    {
        ["prompt_table"] = "powers", ["prompt_key"] = "WELL_LAID_PLANS_POWER.selectionScreenPrompt",
        ["min_select"] = 0, ["max_select"] = amount, ["require_manual_confirmation"] = true,
        ["cancelable"] = false, ["filter_target_model_id"] = "WELL_LAID_PLANS_POWER"
    };
    static void SlipperyBridgeLoop()
    {
        var fixture = JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(
            AppContext.BaseDirectory, "fixtures/slippery-bridge-loop.json")))!;
        var before = fixture["before"]!.ToJsonString();
        var after = fixture["after"]!.ToJsonString();
        var versionBefore = fixture["before_version"]!.GetValue<long>();
        var versionAfter = fixture["after_version"]!.GetValue<long>();
        var hold = Action("choose_event_option", ("index", 1));
        void Case(bool expected, string oldState, string newState, string reason,
            long? oldVersion = null, long? newVersion = null, bool ready = true)
        {
            var result = ActionPostconditions.Reached(hold, Snap(oldState, oldVersion ?? versionBefore),
                Snap(newState, newVersion ?? versionAfter, ready));
            if (result != expected) throw new Exception($"Slippery Bridge {reason}: expected {expected}, got {result}");
            checks++;
        }
        Case(true, before, after, "captured reroll and paid cost");
        if (ActionPostconditions.Reached(Action("choose_event_option", ("index", 0)),
            Snap(before, versionBefore), Snap(after, versionAfter)))
            throw new Exception("Slippery Bridge reroll cannot settle Overcome");
        checks++;
        // Other repeatable events (Tablet of Truth) keep the same option; a changed body proves the click landed.
        var tabletBefore = Rewrite(before, n => n["event"]!["event_id"] = "TABLET_OF_TRUTH");
        var tabletAfter = Rewrite(after, n => n["event"]!["event_id"] = "TABLET_OF_TRUTH");
        var tabletChoice = Action("choose_event_option", ("index", 0));
        if (!ActionPostconditions.Reached(tabletChoice, Snap(tabletBefore, versionBefore), Snap(tabletAfter, versionAfter)))
            throw new Exception("Repeated event with changed body must settle");
        checks++;
        if (ActionPostconditions.Reached(tabletChoice, Snap(tabletBefore, versionBefore), Snap(tabletAfter, versionBefore)))
            throw new Exception("Repeated event without a newer version cannot settle");
        checks++;
        if (ActionPostconditions.Reached(hold, Snap(before, versionBefore, false), Snap(after, versionAfter)))
            throw new Exception("Slippery Bridge unavailable original choice cannot settle");
        checks++;
        var actions = Snap(after, versionAfter).Actions;
        if (!actions.Any(x => x.Action == "choose_event_option" && (int)x.Arguments["index"]! == 1))
            throw new Exception("Slippery Bridge internal ready state must expose Hold On");
        checks++;
        Case(false, before, after, "public pending mask is not internal readiness", ready: false);
        Case(false, before, after, "same version", newVersion: versionBefore);
        Case(false, before, after, "older version", newVersion: versionBefore - 1);
        Case(false, before, Rewrite(after, n => n["player"]!["hp"] = 57), "description without paid HP");
        Case(false, before, Rewrite(after, n => n["player"]!["hp"] = 55), "wrong HP loss");
        Case(true, before, Rewrite(after, n => n["event"]!["body"] = JsonNode.Parse(before)!["event"]!["body"]!.DeepClone()), "body may repeat");
        Case(true, before, Rewrite(after, n => n["event"]!["options"]![0]!["description"] = "Flick-Flack is removed from your Deck."), "candidate description may repeat");
        Case(true, before, Rewrite(after, n => { n["event"]!["options"]![0]!["description"] = "Flick-Flack is removed from your Deck."; n["event"]!["options"]![0]!["keywords"]![0]!["name"] = "Flick-Flack"; }), "same card may be rerolled");
        Case(true, before, Rewrite(after, n => n["event"]!["options"]![0]!.AsObject().Remove("keywords")), "candidate hover tip is auxiliary");
        Case(false, before, Rewrite(after, n => n["event"]!["options"]![0]!["title"] = "Different choice"), "wrong candidate option");
        Case(false, before, Rewrite(after, n => n["event"]!["options"]![1]!["description"] = "Lose 3 HP. The card in the above option is randomized."), "HP and reroll without next cost");
        Case(false, before, Rewrite(after, n => n["event"]!["options"]![1]!["description"] = "Lose 5 HP. The card in the above option is randomized."), "nonconsecutive next cost");
        Case(false, before, Rewrite(after, n => n["event"]!["options"]![1]!["description"] = "Gain 4 HP. The card in the above option is randomized."), "different cost semantics");
        Case(false, before, Rewrite(after, n => n["event"]!["options"]![1]!["description"] = "Lose 4 Gold. The card in the above option is randomized."), "other numeric cost is not HP");
        Case(false, before, Rewrite(after, n => n["event"]!["options"]![1]!["description"] = "Pay 4 HP to reroll."), "unknown HP grammar");
        Case(false, before, Rewrite(after, n => n["event"]!["options"]![1]!.AsObject().Remove("description")), "missing cost description");
        Case(false, before, Rewrite(after, n => n["event"]!["options"]![1]!["description"] = "The card in the above option is randomized."), "missing cost text");
        Case(false, before, Rewrite(after, n => n["event"]!["options"]![1]!["description"] = "Lose 4 HP and 2 Gold."), "ambiguous displayed cost");
        Case(false, before, Rewrite(after, n => n["event"]!["options"]![1]!["index"] = 0), "wrong option index");
        Case(false, before, Rewrite(after, n => n["event"]!.AsObject().Remove("options")), "missing options");
        Case(false, before, Rewrite(after, n => n["event"]!["options"]![1]!["actionable"] = false), "next hold is unavailable");
        Case(false, before, Rewrite(after, n => n["event"]!["options"]![1]!["was_chosen"] = true), "next hold is already chosen");
        Case(false, before, Rewrite(after, n => n["engine"]!["action_queue_idle"] = false), "engine is busy");
        foreach (var key in new[] { "is_ancient", "in_dialogue" })
        {
            Case(false, Rewrite(before, n => n["event"]!.AsObject().Remove(key)), after, "missing before " + key);
            Case(false, before, Rewrite(after, n => n["event"]!.AsObject().Remove(key)), "missing after " + key);
        }
        foreach (var key in new[] { "is_locked", "is_proceed", "was_chosen" })
            foreach (var index in new[] { 0, 1 })
            {
                Case(false, Rewrite(before, n => n["event"]!["options"]![index]!.AsObject().Remove(key)), after, $"missing before option {index} {key}");
                Case(false, before, Rewrite(after, n => n["event"]!["options"]![index]!.AsObject().Remove(key)), $"missing after option {index} {key}");
            }
        foreach (var key in new[] { "session_id", "run_id" })
        {
            Case(false, before, Rewrite(after, n => n["context"]![key] = "other"), "wrong " + key);
            Case(false, before, Rewrite(after, n => n["context"]!.AsObject().Remove(key)), "missing " + key);
        }
        foreach (var key in new[] { "seed", "act", "floor" })
        {
            Case(false, before, Rewrite(after, n => n["run"]![key] = "other"), "wrong " + key);
            Case(false, before, Rewrite(after, n => n["run"]!.AsObject().Remove(key)), "missing " + key);
        }
        Case(false, before, Rewrite(after, n => n["event"]!["event_id"] = "OTHER_EVENT"), "wrong event");
        Case(false, before, Rewrite(after, n => n["event"]!.AsObject().Remove("event_id")), "missing event ID");
        Case(false, before, Rewrite(after, n => n["player"]!.AsObject().Remove("hp")), "missing after HP");
        Case(false, Rewrite(before, n => n["player"]!.AsObject().Remove("hp")), after, "missing before HP");
        var second = Rewrite(after, n => { n["player"]!["hp"] = 50; n["event"]!["options"]![1]!["description"] = "Lose 5 HP. The card in the above option is randomized."; });
        Case(true, after, second, "second Hold On can retain body and card", versionAfter, versionAfter + 1);
        var loopBefore = Rewrite(after, n => { n["player"]!["hp"] = 50; n["event"]!["body"] = "Repeated loop text"; n["event"]!["options"]![1]!["description"] = "Lose 9 HP. The card in the above option is randomized."; });
        var loopAfter = Rewrite(loopBefore, n => { n["player"]!["hp"] = 41; n["event"]!["options"]![1]!["description"] = "Lose 10 HP. The card in the above option is randomized."; });
        Case(true, loopBefore, loopAfter, "LOOP body and candidate can both remain the same", versionAfter + 2, versionAfter + 3);
        Case(false, loopBefore, Rewrite(loopAfter, n => n["player"]!["hp"] = 42), "LOOP wrong HP loss", versionAfter + 2, versionAfter + 3);
        Case(false, loopBefore, Rewrite(loopAfter, n => n["event"]!["options"]![1]!["description"] = "Lose 11 HP. The card in the above option is randomized."), "LOOP wrong next cost", versionAfter + 2, versionAfter + 3);
        var chineseBefore = Rewrite(before, n => n["event"]!["options"]![1]!["description"] = "失去3点生命。重新随机上方选项中的卡牌。");
        var chineseAfter = Rewrite(after, n => n["event"]!["options"]![1]!["description"] = "失去4点生命。重新随机上方选项中的卡牌。");
        Case(true, chineseBefore, chineseAfter, "captured Chinese cost grammar");
        Case(false, chineseBefore, Rewrite(chineseAfter, n => n["event"]!["options"]![1]!["description"] = "失去4金币。重新随机上方选项中的卡牌。"), "unknown localized numeric cost");
    }
    static void NextRoundSelectorContinuation()
    {
        // State projections from lane02/game08 request ...-00178: before v773,
        // after v784. The pending public observation has ready=false and no
        // actions; the internal SnapshotStore evaluates this selector as ready.
        var fixture = JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(
            AppContext.BaseDirectory, "fixtures/next-round-discard.json")))!;
        var before = fixture["before"]!.ToJsonString();
        var after = fixture["after"]!.ToJsonString();
        var end = Action("end_turn");
        void Case(bool expected, string oldState, string newState, string reason,
            long oldVersion = 773, long newVersion = 784, bool ready = true)
        {
            var result = ActionPostconditions.Reached(end, Snap(oldState, oldVersion),
                Snap(newState, newVersion, ready));
            if (result != expected) throw new Exception($"{reason}: expected {expected}, got {result}");
            checks++;
        }
        Case(true, before, after, "captured next-round discard selector settles end turn");
        var available = Snap(after, 784).Actions.Where(x => x.Action == "combat_select_card").ToArray();
        if (available.Length != 6 || available.Select(x => (int)x.Arguments["card_index"]!).Distinct().Count() != 6)
            throw new Exception("captured selector must expose all six actionable card indices");
        checks++;
        Case(false, before, after, "stale snapshot version", newVersion: 773);
        Case(false, before, after, "older snapshot version", newVersion: 772);
        Case(false, before, after, "internal selector is not ready", ready: false);
        foreach (var key in new[] { "session_id", "run_id", "combat_epoch" })
        {
            Case(false, before, Rewrite(after, n => n["context"]![key] = "other"), "changed " + key);
            Case(false, before, Rewrite(after, n => n["context"]!.AsObject().Remove(key)), "missing " + key);
        }
        foreach (var key in new[] { "seed", "act", "floor" })
        {
            Case(false, before, Rewrite(after, n => n["run"]![key] = "other"), "changed " + key);
            Case(false, before, Rewrite(after, n => n["run"]!.AsObject().Remove(key)), "missing " + key);
        }
        foreach (var round in new[] { 1, 3, 0 })
            Case(false, before, Rewrite(after, n => n["battle"]!["round"] = round), "not exactly next round: " + round);
        Case(false, before, Rewrite(after, n => n["battle"]!["turn"] = "enemy"), "not player turn");
        Case(false, before, Rewrite(after, n => n["battle"]!["is_play_phase"] = true), "already in play phase");
        Case(false, before, Rewrite(after, n => n["battle"]!["actions_disabled"] = false), "not disabled while selector is active");
        Case(false, before, Rewrite(after, n => n["battle"]!["can_end_turn"] = true), "end turn still offered");
        Case(false, before, Rewrite(after, n => n["engine"]!["action_queue_idle"] = true), "engine no longer awaiting selector");
        Case(false, Rewrite(before, n => n["battle"]!["can_end_turn"] = false), after, "original end turn unavailable");
        Case(false, Rewrite(before, n => n["engine"]!["action_queue_idle"] = false), after, "original engine busy");
        Case(false, before, Rewrite(after, n => n["ui_overlay"] = new JsonObject()), "blocking overlay");
        Case(false, Rewrite(before, n => n["hand_select"] = new JsonObject()), after, "selector already existed");
        Case(false, before, Rewrite(after, n => n["hand_select"]!["selector"]!["invocation_id"] = null), "unknown invocation");
        Case(false, before, Rewrite(after, n => n["hand_select"]!["selector"]!["invocation_id"] = ""), "empty invocation");
        Case(false, before, Rewrite(after, n => n["hand_select"]!["selector"]!["invocation_id"] = "other-session:selection-1"), "invocation from another session");
        Case(false, before, Rewrite(after, n => n["hand_select"]!["selector"]!["invocation_id"] = "46334219193a43d485d93f1ccb52a814:selection-unknown"), "malformed invocation handle");
        foreach (var key in new[] { "prompt_table", "prompt_key", "min_select", "max_select",
            "require_manual_confirmation", "cancelable", "filter_target_model_id" })
            Case(false, before, Rewrite(after, n => n["hand_select"]!["selector"]!.AsObject().Remove(key)), "incomplete selector " + key);
        Case(false, before, Rewrite(after, n => n["hand_select"]!["selector"]!["prompt_key"] = "OTHER"), "unknown selector origin");
        Case(false, before, Rewrite(after, n => n["hand_select"]!["selector"]!["min_select"] = 0), "wrong selection minimum");
        Case(false, before, Rewrite(after, n => n["hand_select"]!["selector"]!["max_select"] = 2), "wrong selection maximum");
        Case(false, before, Rewrite(after, n => n["hand_select"]!["mode"] = "upgrade_select"), "wrong selector mode");
        Case(false, before, Rewrite(after, n => n["hand_select"]!["can_confirm"] = true), "selector not in unselected initial state");
        Case(false, before, Rewrite(after, n => n["hand_select"]!["cards"]![0]!["actionable"] = false), "ineligible card");
        Case(false, before, Rewrite(after, n => n["hand_select"]!["cards"]![0]!["selected"] = true), "already selected card");
        Case(false, before, Rewrite(after, n => n["hand_select"]!["cards"]![0]!["instance_id"] = "other-card"), "UI identity mismatch");
        Case(false, before, Rewrite(after, n => n["hand_select"]!["cards"]![0]!["id"] = "OTHER"), "UI card mismatch");
        Case(false, before, Rewrite(after, n => n["hand_select"]!["cards"]![0]!["is_upgraded"] = true), "UI upgrade mismatch");
        Case(false, before, Rewrite(after, n => n["hand_select"]!["cards"]![0]!["index"] = 1), "duplicate UI index");
        Case(false, before, Rewrite(after, n => n["hand_select"]!["cards"]!.AsArray().RemoveAt(0)), "incomplete UI card list");
        Case(false, before, Rewrite(after, n => n["player"]!["hand"]![0]!["instance_id"] = "other-card"), "player hand identity mismatch");
        Case(false, before, Rewrite(after, n => n["hand_select"]!["selected_instances"] = new JsonArray("chosen")), "selector already has selection");
    }
    static void ShopPurchaseSettlesByGold()
    {
        const string before = "{\"state_type\":\"shop\",\"player\":{\"gold\":200},\"shop\":{\"inventory_open\":true,\"items\":[{\"index\":3,\"category\":\"relic\",\"price\":120,\"is_stocked\":true}]}}";
        var buy = Action("shop_purchase", ("index", 3));
        var afterGold = before.Replace("\"gold\":200", "\"gold\":80");
        if (!ActionPostconditions.Reached(buy, Snap(before, 5), Snap(afterGold, 6)))
            throw new Exception("gold reduced by the price must settle a purchase");
        checks++;
        if (ActionPostconditions.Reached(buy, Snap(before, 5), Snap(before.Replace("\"gold\":200", "\"gold\":199"), 6)))
            throw new Exception("a different gold change cannot settle a purchase");
        checks++;
        if (ActionPostconditions.Reached(buy, Snap(before, 5), Snap(afterGold, 5)))
            throw new Exception("gold change without a newer version cannot settle");
        checks++;
    }
    static void EndTurnIntoBossChoice()
    {
        const string combat = "{\"state_type\":\"boss\",\"context\":{\"session_id\":\"s\",\"run_id\":\"r\",\"combat_epoch\":\"e\"},\"battle\":{\"round\":1,\"turn\":\"player\",\"enemies\":[]}}";
        const string choose = "{\"state_type\":\"card_select\",\"context\":{\"session_id\":\"s\",\"run_id\":\"r\",\"combat_epoch\":\"e\"},\"card_select\":{\"screen_type\":\"choose\",\"cards\":[]}}";
        var endTurn = Action("end_turn");
        const string pileSelect = "{\"state_type\":\"card_select\",\"context\":{\"session_id\":\"s\",\"run_id\":\"r\",\"combat_epoch\":\"e\"},\"card_select\":{\"screen_type\":\"NCombatPileCardSelectScreen\",\"cards\":[]}}";
        if (!ActionPostconditions.Reached(endTurn, Snap(combat, 5), Snap(pileSelect, 6)))
            throw new Exception("end turn into a combat pile selection must settle");
        checks++;
        if (!ActionPostconditions.Reached(endTurn, Snap(combat, 5), Snap(choose, 6)))
            throw new Exception("end turn into a boss choose screen must settle");
        checks++;
        const string dummyEvent = "{\"state_type\":\"event\",\"context\":{\"session_id\":\"s\",\"run_id\":\"r\",\"combat_epoch\":null},\"event\":{\"event_id\":\"BATTLEWORN_DUMMY\",\"options\":[{\"index\":0,\"is_proceed\":true}]}}";
        var combatInRun = combat;
        if (!ActionPostconditions.Reached(endTurn, Snap(combatInRun, 5), Snap(dummyEvent, 6)))
            throw new Exception("end turn that ends the fight into the event result page must settle");
        checks++;
        if (ActionPostconditions.Reached(endTurn, Snap(combatInRun, 5), Snap(dummyEvent, 5)))
            throw new Exception("event page without a newer version cannot settle end turn");
        checks++;
        if (ActionPostconditions.Reached(endTurn, Snap(combatInRun, 5), Snap(dummyEvent.Replace("\"run_id\":\"r\"", "\"run_id\":\"other\""), 6)))
            throw new Exception("event page of another run cannot settle end turn");
        checks++;
        if (ActionPostconditions.Reached(endTurn, Snap(combat, 5), Snap(choose, 5)))
            throw new Exception("choose screen without a newer version cannot settle end turn");
        checks++;
        if (ActionPostconditions.Reached(endTurn, Snap(combat, 5), Snap(choose.Replace("\"run_id\":\"r\"", "\"run_id\":\"other\""), 6)))
            throw new Exception("choose screen from another run cannot settle end turn");
        checks++;
    }
    static void SequentialDiscardConfirmation()
    {
        var fixturePath = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures/lane05-sequential-discard.json");
        var fixture = JsonNode.Parse(System.IO.File.ReadAllText(fixturePath))!;
        var before = fixture["before"]!.ToJsonString();
        var after = fixture["after"]!.ToJsonString();
        var confirm = Action("combat_confirm_selection");
        Check(false, confirm, before, after,
            "saved 1058-to-1062 case proves a discard effect, but has no selector invocation, session or pile instance evidence");

        // These extra fields model the minimum future runtime evidence; the
        // captured game states above do not contain any of them.
        var selectedId = fixture["before"]!["hand_select"]!["selected_instances"]![0]!.GetValue<string>();
        var enrichedBefore = Rewrite(before, n =>
        {
            n["context"]!["session_id"] = "session-1";
            n["hand_select"]!["selector"]!["invocation_id"] = "invocation-1";
            n["player"]!["pile_identity_complete"] = true;
            var discard = n["player"]!["discard_pile"]!.AsArray();
            for (var i = 0; i < discard.Count; i++)
            {
                discard[i]!["id"] = "PRIOR_" + i;
                discard[i]!["instance_id"] = "prior-discard-" + i;
            }
        });
        var enrichedAfter = Rewrite(after, n =>
        {
            n["context"]!["session_id"] = "session-1";
            n["hand_select"]!["selector"]!["invocation_id"] = "invocation-2";
            n["player"]!["pile_identity_complete"] = true;
            foreach (var pileName in new[] { "discard_pile", "draw_pile", "exhaust_pile" })
            {
                var pile = n["player"]![pileName]!.AsArray();
                for (var i = 0; i < pile.Count; i++)
                {
                    pile[i]!["id"] = pileName == "discard_pile" && i < 8 ? "PRIOR_" + i : "OTHER_" + i;
                    pile[i]!["instance_id"] = pileName == "discard_pile" && i < 8
                        ? "prior-discard-" + i : pileName + "-" + i;
                }
            }
            var discarded = n["player"]!["discard_pile"]!.AsArray().Last()!;
            discarded["id"] = "PREDATOR";
            discarded["instance_id"] = selectedId;
        });
        Check(true, confirm, enrichedBefore, enrichedAfter,
            "new invocation and selected physical instance in actual discard prove sequential confirmation");
        // Confirming an end-of-turn selection that lands on the next round's selector settles too.
        var nextRoundAfter = Rewrite(enrichedAfter, n =>
            n["battle"]!["round"] = JsonNode.Parse(enrichedBefore)!["battle"]!["round"]!.GetValue<int>() + 1);
        if (!ActionPostconditions.Reached(confirm, Snap(enrichedBefore, 10), Snap(nextRoundAfter, 11)))
            throw new Exception("confirm landing on the next round's selector must settle");
        checks++;
        if (ActionPostconditions.Reached(confirm, Snap(enrichedBefore, 10), Snap(nextRoundAfter, 10)))
            throw new Exception("next round selector without a newer version cannot settle");
        checks++;
        var skippedRounds = Rewrite(enrichedAfter, n =>
            n["battle"]!["round"] = JsonNode.Parse(enrichedBefore)!["battle"]!["round"]!.GetValue<int>() + 2);
        if (ActionPostconditions.Reached(confirm, Snap(enrichedBefore, 10), Snap(skippedRounds, 11)))
            throw new Exception("skipping two rounds is not the next-round selector");
        checks++;
        var notReady = Snap(enrichedAfter);
        notReady = new AgentSnapshot(notReady.Version, notReady.StateHash, notReady.State, notReady.Actions,
            false, notReady.StateType, notReady.ObservedAt);
        if (ActionPostconditions.Reached(confirm, Snap(enrichedBefore), notReady))
            throw new Exception("new selector must be interaction-ready before confirmation settles");
        checks++;
        Check(false, confirm, enrichedBefore, Rewrite(enrichedAfter, n =>
            n["hand_select"]!["selector"]!["invocation_id"] = "invocation-1"),
            "same selector during confirmation animation is not a new invocation");
        Check(false, confirm, enrichedBefore, Rewrite(enrichedBefore, n =>
        {
            n["hand_select"]!["selected_cards"] = new JsonArray();
            n["hand_select"]!["selected_instances"] = new JsonArray();
            n["hand_select"]!["can_confirm"] = false;
        }), "cancelling the selection in the same invocation cannot settle confirmation");
        Check(false, confirm, enrichedBefore, Rewrite(enrichedAfter, n =>
            n["player"]!["discard_pile"]!.AsArray().Last()!["instance_id"] = "different-predator"),
            "same card ID in discard does not identify the selected instance");
        Check(false, confirm, enrichedBefore, Rewrite(enrichedAfter, n =>
            n["player"]!["discard_pile"]!.AsArray().Last()!.AsObject().Remove("instance_id")),
            "discard pile without a physical instance is insufficient");
        Check(false, confirm, enrichedBefore, Rewrite(enrichedAfter, n =>
            n["player"]!["discard_pile"]!.AsArray()[0]!.AsObject().Remove("instance_id")),
            "incomplete pile identity makes duplicate-instance checks unreliable");
        Check(false, confirm, enrichedBefore, Rewrite(enrichedAfter, n =>
            n["player"]!["pile_identity_complete"] = false),
            "producer reports incomplete pile identity even if listed cards look usable");
        Check(false, confirm, enrichedBefore, Rewrite(enrichedAfter, n =>
            n["player"]!["discard_pile_count"] = 10),
            "discard counter must agree with the actual pile");
        Check(false, confirm, enrichedBefore, Rewrite(enrichedAfter, n =>
            n["hand_select"]!["selected_cards"] = new JsonArray(new JsonObject { ["instance_id"] = selectedId })),
            "selection toggle or incomplete reset does not prove new selector is ready");
        Check(false, confirm, enrichedBefore, Rewrite(enrichedAfter, n =>
            n["hand_select"]!["selector"]!["prompt_key"] = "OTHER_DISCARD"),
            "next selector must retain the observed discard contract");
        Check(false, confirm, enrichedBefore, Rewrite(enrichedAfter, n =>
            n["context"]!["combat_epoch"] = "another-combat"), "combat epoch must agree");
        Check(false, confirm, enrichedBefore, Rewrite(enrichedAfter, n =>
            n["context"]!["run_id"] = "another-run"), "run must agree");
        Check(false, confirm, enrichedBefore, Rewrite(enrichedAfter, n =>
            n["context"]!["session_id"] = "another-session"), "session must agree");

        var incidentalHandChange = Rewrite(enrichedAfter, n =>
        {
            var hand = n["player"]!["hand"]!.AsArray();
            var candidates = n["hand_select"]!["cards"]!.AsArray();
            hand.RemoveAt(0);
            candidates.RemoveAt(0);
            hand.Add(new JsonObject { ["index"] = 3, ["id"] = "PREDATOR", ["name"] = "Predator", ["instance_id"] = "another-predator" });
            candidates.Add(new JsonObject { ["index"] = 3, ["id"] = "PREDATOR", ["name"] = "Predator", ["instance_id"] = "another-predator", ["selected"] = false, ["actionable"] = true });
        });
        Check(true, confirm, enrichedBefore, incidentalHandChange,
            "unrelated draw and removal do not hide the selected instance's verified transfer");
        Check(false, confirm, enrichedBefore, Rewrite(incidentalHandChange, n =>
        {
            n["player"]!["discard_pile"]!.AsArray().Last()!["instance_id"] = "another-predator";
            n["player"]!["hand"]!.AsArray().Last()!["instance_id"] = selectedId;
            n["hand_select"]!["cards"]!.AsArray().Last()!["instance_id"] = selectedId;
        }), "another copy with the same card ID cannot stand in for the chosen instance");
    }
    static void PurchaseAndTutorialContinuations()
    {
        // Condensed observed facts from H4KX9QD77H F12, request ...-00141.
        // Preserve every eligible Attack, including duplicates and the upgraded Strike.
        const string hammerShop = """
            {"state_type":"shop","context":{"run_id":"ccd931c658e24596938b31d1e55b6114"},
             "run":{"act":1,"floor":12,"ascension":10,"seed":"H4KX9QD77H"},
             "shop":{"inventory_open":true,"items":[
               {"index":8,"category":"relic","relic_id":"REPTILE_TRINKET","price":235,"is_stocked":true},
               {"index":9,"category":"relic","relic_id":"GNARLED_HAMMER","price":217,"is_stocked":true}]},
             "player":{"gold":529,"relics":[{"id":"RING_OF_THE_SNAKE"},{"id":"LOST_COFFER"},{"id":"RIPPLE_BASIN"}],"deck":[
               {"id":"STRIKE_SILENT","type":"Attack","is_upgraded":false},
               {"id":"STRIKE_SILENT","type":"Attack","is_upgraded":false},
               {"id":"STRIKE_SILENT","type":"Attack","is_upgraded":false},
               {"id":"STRIKE_SILENT","type":"Attack","is_upgraded":false},
               {"id":"STRIKE_SILENT","type":"Attack","is_upgraded":true},
               {"id":"NEUTRALIZE","type":"Attack","is_upgraded":false},
               {"id":"SLICE","type":"Attack","is_upgraded":false},
               {"id":"PRECISE_CUT","type":"Attack","is_upgraded":false},
               {"id":"PREDATOR","type":"Attack","is_upgraded":false},
               {"id":"DASH","type":"Attack","is_upgraded":false}]}}
            """;
        const string hammerSelection = """
            {"state_type":"card_select","context":{"run_id":"ccd931c658e24596938b31d1e55b6114"},
             "run":{"act":1,"floor":12,"ascension":10,"seed":"H4KX9QD77H"},
             "card_select":{"screen_type":"NDeckEnchantSelectScreen","prompt":"选择3张牌来附魔。",
               "preview_showing":false,"can_cancel":false,"can_confirm":true,"cards":[
               {"index":0,"id":"STRIKE_SILENT","type":"Attack","is_upgraded":false,"actionable":true,"selected":false},
               {"index":1,"id":"STRIKE_SILENT","type":"Attack","is_upgraded":false,"actionable":true,"selected":false},
               {"index":2,"id":"STRIKE_SILENT","type":"Attack","is_upgraded":false,"actionable":true,"selected":false},
               {"index":3,"id":"STRIKE_SILENT","type":"Attack","is_upgraded":false,"actionable":true,"selected":false},
               {"index":4,"id":"STRIKE_SILENT","type":"Attack","is_upgraded":true,"actionable":true,"selected":false},
               {"index":5,"id":"NEUTRALIZE","type":"Attack","is_upgraded":false,"actionable":true,"selected":false},
               {"index":6,"id":"SLICE","type":"Attack","is_upgraded":false,"actionable":true,"selected":false},
               {"index":7,"id":"PRECISE_CUT","type":"Attack","is_upgraded":false,"actionable":true,"selected":false},
               {"index":8,"id":"PREDATOR","type":"Attack","is_upgraded":false,"actionable":true,"selected":false},
               {"index":9,"id":"DASH","type":"Attack","is_upgraded":false,"actionable":true,"selected":false}]},
             "player":{"gold":312,"relics":[{"id":"RING_OF_THE_SNAKE"},{"id":"LOST_COFFER"},{"id":"RIPPLE_BASIN"},{"id":"GNARLED_HAMMER"}]},
             "engine":{"action_queue_idle":true}}
            """;
        var buy = Action("shop_purchase", ("index", 9));
        Check(true, buy, hammerShop, hammerSelection, "paid Hammer purchase reaches its attack enchantment continuation");
        var childActions = Snap(hammerSelection).Actions;
        if (childActions.Count(a => a.Action == "select_card") != 10 || !childActions.Any(a => a.Action == "confirm_selection"))
            throw new Exception("Hammer continuation must advertise child selections and confirmation");
        checks++;
        Check(true, Action("select_card", ("index", 9)), hammerSelection,
            Rewrite(hammerSelection, n => n["card_select"]!["cards"]![9]!["selected"] = true), "Hammer child selection has its own postcondition");
        Check(true, buy, hammerShop, Rewrite(hammerSelection, n => n["player"]!["relics"]!.AsArray().RemoveAt(3)),
            "pickup may be waiting on child selection before relic inventory publication");
        Check(true, buy, hammerShop, Rewrite(hammerSelection, n => n["shop"] = JsonNode.Parse(hammerShop)!["shop"]!.DeepClone()),
            "stock may remain stocked until the pickup continuation finishes");
        Check(false, Action("shop_purchase", ("index", 8)), hammerShop, hammerSelection, "another purchased target cannot claim Hammer continuation");
        foreach (var gold in new[] { 529, 313, 311, -1 })
            Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["player"]!["gold"] = gold), "unpaid or mismatched payment: " + gold);
        Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["player"]!.AsObject().Remove("gold")), "unknown payment fails closed");
        Check(false, buy, Rewrite(hammerShop, n => n["shop"]!["items"]![1]!["category"] = "card"), hammerSelection, "wrong purchase category");
        Check(false, buy, Rewrite(hammerShop, n => n["shop"]!["items"]![1]!["relic_id"] = "MANGO"), hammerSelection, "wrong purchased relic");
        Check(false, buy, Rewrite(hammerShop, n => n["shop"]!["items"]![1]!["is_stocked"] = false), hammerSelection, "unstocked target");
        Check(false, buy, Rewrite(hammerShop, n => n["shop"]!["items"]![1]!["price"] = 0), hammerSelection, "unknown free pickup semantics");
        Check(false, buy, Rewrite(hammerShop, n => n["shop"]!["items"]![1]!.AsObject().Remove("price")), hammerSelection, "unknown price");
        Check(false, buy, Rewrite(hammerShop, n => n["shop"]!["inventory_open"] = false), hammerSelection, "closed shop is not purchase origin");
        Check(false, buy, Rewrite(hammerShop, n => n["card_select"] = JsonNode.Parse(hammerSelection)!["card_select"]!.DeepClone()), hammerSelection, "preexisting selection is not a new purchase continuation");
        Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["card_select"]!["screen_type"] = "NCardGridSelectionScreen"), "unrelated selection screen");
        Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["state_type"] = "unknown"), "unknown state never releases purchase");
        Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["card_select"]!["cards"]![0]!["type"] = "Skill"), "wrong selection category");
        Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["card_select"]!["cards"]![0]!["id"] = "OTHER_ATTACK"), "wrong attack target");
        Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["card_select"]!["cards"]![4]!["is_upgraded"] = false), "duplicate card upgrades must match");
        Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["card_select"]!["cards"]!.AsArray().RemoveAt(0)), "missing duplicate attack");
        Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["card_select"]!["cards"] = new JsonArray()), "empty selection is ambiguous");
        Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["card_select"]!["cards"]![0]!["selected"] = true), "already edited selection fails closed");
        Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["card_select"]!["cards"]![0]!.AsObject().Remove("selected")), "unknown selected status fails closed");
        Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["card_select"]!["cards"]![0]!["actionable"] = false), "child target is not actionable");
        Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["card_select"]!["preview_showing"] = true), "old preview cannot settle purchase");
        Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["card_select"]!["can_confirm"] = false), "unready child selection");
        Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["context"]!["run_id"] = "other"), "purchase cannot cross run identity");
        Check(false, buy, Rewrite(hammerShop, n => n["context"]!["run_id"] = null), hammerSelection, "unknown purchase run identity");
        Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["context"]!["run_id"] = null), "missing continuation run identity");
        Check(false, buy, Rewrite(hammerShop, n => n["player"]!.AsObject().Remove("deck")), hammerSelection, "unknown eligible deck");
        Check(false, buy, Rewrite(hammerShop, n => n["player"]!["deck"]!.AsArray().Add(new JsonObject { ["id"] = "UNKNOWN_CARD" })), hammerSelection,
            "a deck card without type cannot be silently excluded from eligible attacks");
        foreach (var type in new string?[] { null, "", "FutureCardType" })
            Check(false, buy, Rewrite(hammerShop, n => n["player"]!["deck"]!.AsArray().Add(new JsonObject { ["id"] = "UNKNOWN_CARD", ["type"] = type })),
                hammerSelection, "unrecognized deck type fails closed: " + (type ?? "null"));
        foreach (var type in new[] { "Skill", "Power", "Status", "Curse" })
            Check(true, buy, Rewrite(hammerShop, n => n["player"]!["deck"]!.AsArray().Add(new JsonObject { ["id"] = "NON_ATTACK", ["type"] = type })),
                hammerSelection, "recognized non-Attack types may be excluded: " + type);
        Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["run"]!.AsObject().Remove("seed")), "unknown continuation seed");
        Check(false, buy, hammerShop, Rewrite(hammerSelection, n => n["run"]!["floor"] = 13), "purchase cannot claim a different room");
        // Preserve the enabled choices, exact FTUE type, and new-run metadata observed
        // in isolated-instance-02/headless-tutorial-{prepared,blocked-evidence}.json.
        const string prompt = """
            {"state_type":"menu","menu_screen":"tutorial_prompt","options":[{"name":"no","enabled":true},{"name":"yes","enabled":true}],
             "ui_overlay":{"type":"NAcceptTutorialsFtue","instance_id":"492260308741"},"context":{"run_id":null},"engine":{"action_queue_idle":true}}
            """;
        const string started = """
            {"state_type":"map","map":{},"run":{"act":1,"floor":0,"ascension":0,"seed":"JHMK5PFGPJ"},
             "context":{"run_id":"467e47a027004768a7e8fe50d89e1362"},"engine":{"action_queue_idle":true}}
            """;
        foreach (var option in new[] { "no", "yes" })
        {
            Check(true, Action("menu_select", ("option", option)), prompt, started, "tutorial consent starts a new run: " + option);
            Check(true, Action("menu_select", ("option", option)), prompt,
                Rewrite(started, n => { n["state_type"] = "event"; n.AsObject().Remove("map"); n["event"] = new JsonObject(); }),
                "tutorial consent may reach the initial event: " + option);
        }
        var no = Action("menu_select", ("option", "no"));
        Check(false, no, Rewrite(prompt, n => n["menu_screen"] = "popup"), started, "generic yes/no popup is not tutorial consent");
        Check(false, no, Rewrite(prompt, n => n["ui_overlay"]!["type"] = "NGenericPopup"), started, "wrong tutorial overlay");
        Check(false, no, Rewrite(prompt, n => n.AsObject().Remove("ui_overlay")), started, "missing identified tutorial overlay");
        Check(false, no, Rewrite(prompt, n => n["options"]![0]!["enabled"] = false), started, "disabled tutorial choice");
        Check(false, no, Rewrite(prompt, n => n["options"]!.AsArray().RemoveAt(0)), started, "unadvertised tutorial choice");
        Check(false, Action("menu_select", ("option", "ok")), prompt, started, "wrong tutorial option");
        foreach (var option in new[] { "continue", "confirm", "embark" })
            Check(false, Action("menu_select", ("option", option)), prompt, started, "generic run launch cannot bypass tutorial choice: " + option);
        Check(false, no, Rewrite(prompt, n => n["context"]!["run_id"] = "old"), started, "consent cannot replace an observed run");
        Check(false, no, Rewrite(prompt, n => n.AsObject().Remove("context")), started, "missing before context cannot prove no prior run");
        Check(false, no, Rewrite(prompt, n => n["context"] = null), started, "null before context cannot prove no prior run");
        Check(false, no, Rewrite(prompt, n => n["context"] = "unknown"), started, "malformed before context cannot prove no prior run");
        Check(false, no, Rewrite(prompt, n => n["context"]!.AsObject().Remove("run_id")), started, "missing prior run_id differs from explicitly observed null");
        Check(false, no, Rewrite(prompt, n => n["run"] = new JsonObject()), started, "unknown before run metadata is not absence of a run");
        Check(false, no, Rewrite(prompt, n => n["run"] = "unknown"), started, "malformed before run metadata is not absence of a run");
        Check(true, no, Rewrite(prompt, n => n["run"] = null), started, "explicit null run agrees with the explicit null before run identity");
        Check(false, no, prompt, Rewrite(started, n => n.AsObject().Remove("run")), "missing run metadata");
        Check(false, no, prompt, Rewrite(started, n => n["context"]!["run_id"] = null), "missing new run identity");
        Check(false, no, prompt, Rewrite(started, n => n["context"]!["run_id"] = " "), "blank new run identity");
        Check(false, no, prompt, Rewrite(started, n => n["run"]!["floor"] = 1), "tutorial launch only acknowledges initial floor");
        Check(false, no, prompt, Rewrite(started, n => n["run"]!["ascension"] = 1), "tutorial launch only acknowledges observed ascension zero");
        Check(false, no, prompt, Rewrite(started, n => n["run"]!["act"] = 2), "wrong initial act");
        Check(false, no, prompt, Rewrite(started, n => n["run"]!["seed"] = null), "unknown new run seed");
        Check(false, no, prompt, Rewrite(started, n => n["engine"]!["action_queue_idle"] = false), "pending engine action is not completed consent");
        Check(false, no, prompt, Rewrite(started, n => n.AsObject().Remove("engine")), "unknown engine status");
        Check(false, no, prompt, Rewrite(started, n => n.AsObject().Remove("map")), "run phase payload missing");
        Check(false, no, prompt, Rewrite(started, n => n["map"] = new JsonArray()), "run phase payload must be an object");
        Check(false, no, prompt, Rewrite(started, n => n["state_type"] = "unknown"), "unknown tutorial destination");
        Check(false, no, prompt, Rewrite(started, n => n["state_type"] = "monster"), "arbitrary run phase cannot settle tutorial consent");
        Check(false, no, prompt, Rewrite(started, n => n["ui_overlay"] = JsonNode.Parse(prompt)!["ui_overlay"]!.DeepClone()), "tutorial consent still visible");
        Check(false, no, prompt, Rewrite(started, n => n["ui_overlay"] = new JsonObject { ["instance_id"] = "492260308741" }), "same overlay with missing type is unresolved consent");
        Check(false, no, prompt, Rewrite(started, n => n["ui_overlay"] = new JsonObject()), "empty overlay does not prove consent disappeared");
        Check(false, no, prompt, Rewrite(started, n => n["ui_overlay"] = "unknown"), "malformed overlay does not prove consent disappeared");
        Check(false, no, prompt, Rewrite(started, n => n["ui_overlay"] = new JsonObject { ["instance_id"] = "new", ["type"] = "NOtherTutorial" }), "unverified replacement overlay remains fail closed");
        Check(true, no, prompt, Rewrite(started, n => n["ui_overlay"] = null), "explicit null after overlay proves consent disappeared");

        // When running in the evidence workspace, also replay both original full
        // snapshots. The condensed facts above remain portable in clean checkouts.
        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "SpireLink.csproj"))) root = root.Parent;
        if (root != null)
        {
            var campaign = System.IO.Path.Combine(root.FullName, "test-results/campaign-20261022/batch-20261022-205214-1ae1fc/run-02");
            var isolated = System.IO.Path.Combine(root.FullName, "test-results/isolated-instance-02");
            var paths = new[] { System.IO.Path.Combine(campaign, "contexts/52c8bbfe9c5d4486a43bdf59d153767e-00141.json"),
                System.IO.Path.Combine(campaign, "shop-selection-blocked-evidence.json"),
                System.IO.Path.Combine(isolated, "headless-tutorial-prepared.json"), System.IO.Path.Combine(isolated, "headless-tutorial-blocked-evidence.json") };
            if (paths.All(System.IO.File.Exists))
            {
                var snapshots = paths.Select(p => JsonNode.Parse(System.IO.File.ReadAllText(p))!).ToArray();
                Check(true, buy, snapshots[0]["state"]!.ToJsonString(), snapshots[1]["observation"]!["state"]!.ToJsonString(), "full captured Hammer continuation");
                Check(true, no, snapshots[2]["observation"]!["state"]!.ToJsonString(), snapshots[3]["observation"]!["state"]!.ToJsonString(), "full captured tutorial launch");
                Console.WriteLine("Captured continuation replays passed: 2");
            }
        }
    }
    static void EndTurnRetentionContinuation()
    {
        // Condensed facts from original request ...93b2...-00246, H4KX9QD77H A10 F17.
        const string before = """
            {"state_type":"boss","battle":{"round":1,"turn":"player","is_play_phase":true,"can_end_turn":true,"actions_disabled":false},
             "run":{"act":1,"floor":17,"ascension":10,"seed":"H4KX9QD77H"},
             "context":{"run_id":"ccd931c658e24596938b31d1e55b6114","combat_epoch":"e692f6b3d7ae4148a395698d9f3e5c78"},
             "engine":{"action_queue_idle":true},"player":{"status":[{"id":"WELL_LAID_PLANS_POWER","amount":1}],
             "deck":[{"id":"WELL_LAID_PLANS","is_upgraded":false}],"hand":[
               {"index":0,"id":"DEFEND_SILENT","type":"Skill","is_upgraded":false,"instance_id":"e692f6b3d7ae4148a395698d9f3e5c78:card-4"},
               {"index":1,"id":"DASH","type":"Attack","is_upgraded":false,"instance_id":"e692f6b3d7ae4148a395698d9f3e5c78:card-5"},
               {"index":2,"id":"STRIKE_SILENT","type":"Attack","is_upgraded":false,"instance_id":"e692f6b3d7ae4148a395698d9f3e5c78:card-6"}]}}
            """;
        var after = Rewrite(before, n =>
        {
            n["state_type"] = "hand_select";
            n["battle"]!["is_play_phase"] = false;
            n["battle"]!["can_end_turn"] = false;
            n["battle"]!["actions_disabled"] = true;
            n["engine"]!["action_queue_idle"] = false;
            var cards = n["player"]!["hand"]!.DeepClone();
            foreach (var card in cards.AsArray()) { card!["actionable"] = true; card["selected"] = false; }
            // Synthetic machine-origin enrichment, independently verified against the
            // current WellLaidPlansPower/PowerModel/CardSelectorPrefs implementation.
            n["hand_select"] = new JsonObject { ["mode"] = "simple_select", ["prompt"] = "选择1张牌保留。", ["cards"] = cards,
                ["can_confirm"] = true, ["selector"] = RetentionSelector(1) };
        });
        var end = Action("end_turn");
        Check(true, end, before, after, "end-turn reaches the known Well Laid Plans retention child without finishing the turn");
        var childActions = Snap(after).Actions;
        if (childActions.Count(a => a.Action == "combat_select_card") != 3 || !childActions.Any(a => a.Action == "combat_confirm_selection"))
            throw new Exception("retention continuation must advertise its child actions");
        checks++;
        Check(true, Action("combat_select_card", ("card_index", 1)), after, Rewrite(after, n =>
        {
            n["hand_select"]!["selected_cards"] = new JsonArray(new JsonObject { ["index"] = 0, ["selected"] = true,
                ["instance_id"] = "e692f6b3d7ae4148a395698d9f3e5c78:card-5", ["name"] = "冲刺" });
            n["hand_select"]!["cards"]!.AsArray().RemoveAt(1);
        }), "retention child selection still requires its own observable result");
        {
            const string shopBefore = """{"state_type":"shop","run":{"floor":20},"player":{"gold":300},"shop":{"inventory_open":true,"items":[{"index":3,"category":"relic","relic_id":"DOLLYS_MIRROR","price":207,"is_stocked":true}]}}""";
            const string cardPick = """{"state_type":"card_select","run":{"floor":20},"player":{"gold":93},"card_select":{"cards":[{"index":0,"actionable":true}],"can_confirm":false}}""";
            const string cardPickNoPay = """{"state_type":"card_select","run":{"floor":20},"player":{"gold":300},"card_select":{"cards":[{"index":0,"actionable":true}],"can_confirm":false}}""";
            if (!ActionPostconditions.Reached(Action("shop_purchase", ("index", 3)), Snap(shopBefore, 1), Snap(cardPick, 2)))
                throw new Exception("a relic that opens a card pick is settled once its price left the purse");
            if (ActionPostconditions.Reached(Action("shop_purchase", ("index", 3)), Snap(shopBefore, 1), Snap(cardPickNoPay, 2)))
                throw new Exception("a card selection without the payment is not a purchase");
            checks += 2;
        }
        var upgradePick = Rewrite(after, n => { n["hand_select"]!["mode"] = "upgrade_select"; n["hand_select"]!["can_confirm"] = false; });
        Check(true, Action("combat_select_card", ("card_index", 1)), upgradePick, Rewrite(upgradePick, n =>
        {
            n["hand_select"]!["can_confirm"] = true;
            var kept = n["hand_select"]!["cards"]!.AsArray();
            kept.RemoveAt(2); kept.RemoveAt(0);
        }), "picking the card to upgrade moves to the confirm step with only that card left");
        Check(false, Action("combat_select_card", ("card_index", 1)), upgradePick, Rewrite(upgradePick, n =>
            n["hand_select"]!["cards"]!.AsArray().RemoveAt(0)), "fewer cards without the confirm button is not a completed pick");
        Check(false, end, before, """{"state_type":"hand_select","hand_select":{"cards":[],"can_confirm":true}}""", "arbitrary hand selection is not end-turn completion");
        Check(false, end, before, Rewrite(after, n => { n["hand_select"]!["prompt"] = "选择1张牌消耗。"; n["hand_select"]!["selector"]!["prompt_key"] = "OTHER.selectionScreenPrompt"; }), "unrelated exhaust selector");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!.AsObject().Remove("selector")), "formatted retention text without machine origin fails closed");
        Check(true, end, before, Rewrite(after, n => n["hand_select"]!.AsObject().Remove("prompt")), "formatted prompt is not required when selector origin is known");
        Check(true, end, before, Rewrite(after, n => n["hand_select"]!["prompt"] = "arbitrary translated display text"), "known machine origin is independent of display language");
        foreach (var key in new[] { "prompt_table", "prompt_key", "min_select", "max_select", "require_manual_confirmation", "cancelable", "filter_target_model_id" })
        {
            Check(false, end, before, Rewrite(after, n => n["hand_select"]!["selector"]!.AsObject().Remove(key)), "missing selector field " + key);
            Check(false, end, before, Rewrite(after, n => n["hand_select"]!["selector"]![key] = null), "null selector field " + key);
        }
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["selector"] = "unknown"), "malformed machine selector origin");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["selector"]!["prompt_table"] = "cards"), "wrong localization table");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["selector"]!["filter_target_model_id"] = "OTHER_POWER"), "same prompt with another model filter");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["selector"]!["min_select"] = 1), "wrong retention minimum");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["selector"]!["max_select"] = 2), "maximum must match the power amount");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["selector"]!["require_manual_confirmation"] = false), "wrong manual confirmation preference");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["selector"]!["cancelable"] = true), "wrong cancellation preference");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["mode"] = "upgrade_select"), "wrong selection mode");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["can_confirm"] = false), "retention child is not ready");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["cards"]![0]!["selected"] = true), "selection was already edited");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["selected_cards"] = new JsonArray(new JsonObject { ["name"] = "防御" })), "selected container is not empty");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["selected_instances"] = new JsonArray("card-4")), "selected instance list is not empty");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["selected_cards"] = null), "malformed selected container is unknown");
        Check(true, end, before, Rewrite(after, n => { n["hand_select"]!["selected_cards"] = new JsonArray(); n["hand_select"]!["selected_instances"] = new JsonArray(); }), "explicit empty selection containers");
        Check(true, end, before, Rewrite(after, n => n["hand_select"]!["cards"]![0]!["actionable"] = false), "actual filter may exclude a retained card while other cards remain eligible");
        var partialEligibility = Snap(Rewrite(after, n => n["hand_select"]!["cards"]![0]!["actionable"] = false));
        if (partialEligibility.Actions.Count(a => a.Action == "combat_select_card") != 2 ||
            partialEligibility.Actions.Any(a => a.Action == "combat_select_card" && (int)a.Arguments["card_index"]! == 0))
            throw new Exception("an ineligible retained card must not become a selectable child action");
        checks++;
        Check(false, end, before, Rewrite(after, n => { n["hand_select"]!["cards"]![0]!["actionable"] = false; n["hand_select"]!["selector"]!["filter_target_model_id"] = "OTHER_POWER"; }),
            "partial eligibility cannot bypass canonical selector source validation");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["cards"]![0]!.AsObject().Remove("actionable")), "unknown actual eligibility");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["cards"]![0]!["actionable"] = null), "null is not an observed eligibility boolean");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["cards"]![0]!["actionable"] = "false"), "string is not an observed eligibility boolean");
        Check(false, end, before, Rewrite(after, n => { foreach (var c in n["hand_select"]!["cards"]!.AsArray()) c!["actionable"] = false; }), "no eligible child cards is not a ready retention continuation");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["cards"]![0]!.AsObject().Remove("selected")), "unknown provisional selection");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["cards"]!.AsArray().RemoveAt(0)), "UI must describe the complete current hand even when some cards are ineligible");
        Check(true, end, before, Rewrite(after, n => { n["player"]!["hand"]!.AsArray().RemoveAt(0); n["hand_select"]!["cards"]!.AsArray().RemoveAt(0); }), "earlier turn-end removal is supported when the current hand and UI agree");
        Check(true, end, Rewrite(before, n => { n["player"]!["hand"]![0]!["id"] = "ASCENDERS_BANE"; n["player"]!["hand"]![0]!["type"] = "Curse"; }),
            Rewrite(after, n => { n["player"]!["hand"]!.AsArray().RemoveAt(0); n["hand_select"]!["cards"]!.AsArray().RemoveAt(0); }),
            "Ascenders Bane can leave before the canonical retention child without requiring exhaust-instance data");
        Check(true, end, before, Rewrite(after, n =>
        {
            // Snakebite style: a card that already Retains is in the hand but not offered by the selector.
            var card = n["player"]!["hand"]![0]!.DeepClone();
            card["index"] = 4; card["id"] = "RETAINING_CARD"; card["instance_id"] = "e692f6b3d7ae4148a395698d9f3e5c78:card-98";
            card["kw"] = new JsonArray("Retain");
            n["player"]!["hand"]!.AsArray().Add(card);
        }), "a hand card that already Retains may be absent from the retention UI");
        Check(false, end, before, Rewrite(after, n =>
        {
            var card = n["player"]!["hand"]![0]!.DeepClone();
            card["index"] = 4; card["id"] = "PLAIN_CARD"; card["instance_id"] = "e692f6b3d7ae4148a395698d9f3e5c78:card-97";
            card["kw"] = new JsonArray("Exhaust");
            n["player"]!["hand"]!.AsArray().Add(card);
        }), "a hand card without Retain that is missing from the UI is still rejected");
        Check(true, end, before, Rewrite(after, n =>
        {
            var card = n["player"]!["hand"]![0]!.DeepClone();
            card["index"] = 3; card["id"] = "NEW_CARD"; card["instance_id"] = "e692f6b3d7ae4148a395698d9f3e5c78:card-99";
            n["player"]!["hand"]!.AsArray().Add(card);
            var candidate = card.DeepClone(); candidate["actionable"] = true; candidate["selected"] = false;
            n["hand_select"]!["cards"]!.AsArray().Add(candidate);
        }), "turn-end effects may add a card before the identified retention child");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["cards"]![0]!["instance_id"] = "another-instance"), "UI instance must match the current player hand");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["cards"]![0]!["id"] = "OTHER_CARD"), "wrong candidate card identity");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["cards"]![0]!["is_upgraded"] = true), "candidate metadata differs from the original instance");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["cards"]![0]!["index"] = 1), "ambiguous duplicate candidate index");
        Check(false, end, before, Rewrite(after, n => n["player"]!["hand"]![0]!["instance_id"] = "another-instance"), "player hand must match the current UI instances");
        Check(false, end, before, Rewrite(after, n => n["player"]!["hand"]![0]!.AsObject().Remove("instance_id")), "unknown current hand instance");
        Check(false, end, before, Rewrite(after, n => n["player"]!["hand"]![0]!["type"] = "Unknown"), "unknown current hand card type");
        Check(false, end, before, Rewrite(after, n => n["player"]!["hand"]![0]!["instance_id"] = n["player"]!["hand"]![1]!["instance_id"]!.DeepClone()), "duplicate current hand instance");
        Check(false, end, before, Rewrite(after, n => n["hand_select"]!["cards"]![0]!["instance_id"] = n["hand_select"]!["cards"]![1]!["instance_id"]!.DeepClone()), "duplicate UI instance");
        Check(false, end, before, Rewrite(after, n => n["player"]!.AsObject().Remove("hand")), "missing current hand");
        Check(true, end, Rewrite(before, n => n["player"]!.AsObject().Remove("hand")), after, "canonical child evidence does not depend on reconstructing the pre-turn hand");
        Check(false, end, Rewrite(before, n => n["player"]!["status"] = new JsonArray()), after, "no Well Laid Plans power");
        Check(false, end, before, Rewrite(after, n => n["player"]!["status"] = new JsonArray()), "after power is not observed");
        Check(false, end, Rewrite(before, n => n["player"]!["status"]![0]!["amount"] = 0), after, "inactive power");
        Check(false, end, Rewrite(before, n => n["player"]!["status"]![0]!.AsObject().Remove("amount")), after, "unknown power amount");
        Check(false, end, Rewrite(before, n => n["player"]!["status"]!.AsArray().Add(n["player"]!["status"]![0]!.DeepClone())), after, "ambiguous duplicate power entries");
        Check(false, end, before, Rewrite(after, n => n["player"]!["status"]![0]!["amount"] = 2), "different power amount");
        Check(true, end, Rewrite(before, n => n["player"]!["status"]![0]!["amount"] = 2), Rewrite(after, n => { n["player"]!["status"]![0]!["amount"] = 2; n["hand_select"]!["selector"] = RetentionSelector(2); n["hand_select"]!["prompt"] = "display text for upgraded or stacked amount two"; }), "verified amount-two preferences use the same machine origin");
        Check(true, end, Rewrite(before, n => { n["player"]!["status"]![0]!["amount"] = 2; n["player"]!["deck"]![0]!["is_upgraded"] = true; }),
            Rewrite(after, n => { n["player"]!["status"]![0]!["amount"] = 2; n["player"]!["deck"]![0]!["is_upgraded"] = true; n["hand_select"]!["selector"] = RetentionSelector(2); }),
            "upgraded Well Laid Plans amount two uses the same machine contract");
        Check(true, end, Rewrite(before, n => n["player"]!["status"]![0]!["amount"] = 4),
            Rewrite(after, n => { n["player"]!["status"]![0]!["amount"] = 4; n["hand_select"]!["selector"] = RetentionSelector(4); }),
            "max exceeding the hand size still requires explicit confirmation with minimum zero");
        Check(false, end, Rewrite(before, n => n["battle"]!["can_end_turn"] = false), after, "end turn was not allowed");
        Check(false, end, Rewrite(before, n => n["battle"]!["is_play_phase"] = false), after, "original turn was already ending");
        Check(false, end, Rewrite(before, n => n["battle"]!.AsObject().Remove("actions_disabled")), after, "unknown original action phase");
        Check(false, end, before, Rewrite(after, n => n["battle"]!["is_play_phase"] = true), "generic selection during play phase");
        Check(false, end, before, Rewrite(after, n => n["battle"]!["can_end_turn"] = true), "after end-turn is still offered");
        Check(false, end, before, Rewrite(after, n => n["battle"]!["actions_disabled"] = false), "after combat action phase is not disabled");
        Check(false, end, before, Rewrite(after, n => n["battle"]!["round"] = 2), "different round cannot identify this continuation");
        Check(false, end, before, Rewrite(after, n => n["battle"]!["turn"] = "enemy"), "different turn cannot identify this continuation");
        Check(false, end, before, Rewrite(after, n => n["context"]!["combat_epoch"] = "other-combat"), "cross-combat selection");
        Check(false, end, before, Rewrite(after, n => n["context"]!["run_id"] = "other-run"), "cross-run selection");
        Check(false, end, Rewrite(before, n => n["context"]!.AsObject().Remove("combat_epoch")), after, "unknown original combat identity");
        Check(false, end, before, Rewrite(after, n => n["run"]!["floor"] = 18), "different room");
        foreach (var key in new[] { "run_id", "combat_epoch" })
        {
            Check(false, end, Rewrite(before, n => n["context"]!.AsObject().Remove(key)), after, "missing original context " + key);
            Check(false, end, before, Rewrite(after, n => n["context"]!.AsObject().Remove(key)), "missing child context " + key);
        }
        foreach (var key in new[] { "act", "floor", "ascension", "seed" })
        {
            Check(false, end, Rewrite(before, n => n["run"]!.AsObject().Remove(key)), after, "missing original run " + key);
            Check(false, end, before, Rewrite(after, n => n["run"]!.AsObject().Remove(key)), "missing child run " + key);
            Check(false, end, Rewrite(before, n => n["run"]![key] = key == "seed" ? (JsonNode?)JsonValue.Create(42) : JsonValue.Create("unknown")),
                Rewrite(after, n => n["run"]![key] = key == "seed" ? (JsonNode?)JsonValue.Create(42) : JsonValue.Create("unknown")), "malformed matching run " + key);
        }
        foreach (var key in new[] { "round", "turn", "is_play_phase", "can_end_turn", "actions_disabled" })
        {
            Check(false, end, Rewrite(before, n => n["battle"]!.AsObject().Remove(key)), after, "missing original battle " + key);
            Check(false, end, before, Rewrite(after, n => n["battle"]!.AsObject().Remove(key)), "missing child battle " + key);
        }
        Check(false, end, Rewrite(before, n => n["engine"]!.AsObject().Remove("action_queue_idle")), after, "unknown original engine phase");
        Check(false, end, Rewrite(before, n => n["engine"]!["action_queue_idle"] = false), after, "original engine was already processing a different action");
        Check(false, end, before, Rewrite(after, n => n["engine"]!["action_queue_idle"] = true), "engine no longer awaits this child");
        Check(false, end, before, Rewrite(after, n => n["engine"]!.AsObject().Remove("action_queue_idle")), "unknown engine phase");
        Check(false, end, before, Rewrite(after, n => n["ui_overlay"] = new JsonObject()), "unknown blocking overlay");
        Check(false, end, Rewrite(before, n => n["hand_select"] = JsonNode.Parse(after)!["hand_select"]!.DeepClone()), after, "preexisting selection cannot complete a new end-turn dispatch");

        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "SpireLink.csproj"))) root = root.Parent;
        if (root != null)
        {
            var run = System.IO.Path.Combine(root.FullName, "test-results/campaign-20261022/batch-20261022-205214-1ae1fc/run-02");
            var oldPath = System.IO.Path.Combine(run, "contexts/93b2c2aca8c64a3d969b5e3a96efe54c-00246.json");
            var newPath = System.IO.Path.Combine(run, "end-turn-hand-selection-blocked.json");
            if (System.IO.File.Exists(oldPath) && System.IO.File.Exists(newPath))
            {
                var oldState = JsonNode.Parse(System.IO.File.ReadAllText(oldPath))!["state"]!.ToJsonString();
                var newState = JsonNode.Parse(System.IO.File.ReadAllText(newPath))!["observation"]!["state"]!.ToJsonString();
                Check(false, end, oldState, newState, "original raw capture lacks machine selector origin and must remain fail closed");
                Check(true, end, oldState, Rewrite(newState, n => n["hand_select"]!["selector"] = RetentionSelector(1)),
                    "original raw state with explicitly synthetic, assembly-verified selector enrichment");
                Console.WriteLine("Captured end-turn retention checks passed: raw rejected; synthetic origin enrichment accepted");
            }
            var livePath = System.IO.Path.Combine(root.FullName, "test-results/isolated-instance-02/generations/g0005/retention/end-turn-continuation.json");
            if (System.IO.File.Exists(livePath))
            {
                var live = JsonNode.Parse(System.IO.File.ReadAllText(livePath))!;
                Check(true, end, live["before"]!["state"]!.ToJsonString(), live["after"]!["state"]!.ToJsonString(),
                    "g0005 captured live selector origin settles the retention parent without synthetic enrichment");
                Console.WriteLine("Captured g0005 live retention replay passed: 1");
            }
        }
    }
    static void SlyDiscardPlaysOnDiscard()
    {
        // Captured lane02 ro-hunt5 game 9: Master Planner made Prepared Sly, so discarding Prepared played it on the spot
        // (draw 1, discard 1) and opened a second discard selector while the discarded card was in no pile.
        var fixture = JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures/prepared-sly-discard.json")))!;
        var confirm = Action("combat_confirm_selection");
        var before = fixture["before"]!.ToJsonString();
        var after = fixture["after"]!.ToJsonString();
        Check(true, confirm, before, after, "discarded Sly card played on discard opens the next discard selector");
        Check(false, confirm, before, Rewrite(after, n =>
            n["hand_select"]!["selector"]!["invocation_id"] = fixture["before"]!["hand_select"]!["selector"]!["invocation_id"]!.GetValue<string>()),
            "same selector invocation is not progress");
        var chosenId = fixture["before"]!["hand_select"]!["selected_instances"]![0]!.GetValue<string>();
        Check(false, confirm, before, Rewrite(after, n =>
        {
            var hand = n["player"]!["hand"]!.AsArray();
            hand[hand.Count - 1] = JsonNode.Parse(fixture["before"]!["player"]!["hand"]!.AsArray().Last()!.ToJsonString());
        }), "no card drawn into the hand is not evidence of a nested play");
        Check(false, confirm, before, Rewrite(after, n =>
            n["player"]!["draw_pile"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"PREPARED\",\"instance_id\":\"" + chosenId + "\"}"))),
            "chosen card back in the draw pile is a different outcome");
    }
    static void HandTrickRepeatsItsSelector()
    {
        // Captured lane04 ro-hunt6 seed HUNTRSEED038: Hand Trick played twice (Burst) opens its selector again after the confirm.
        var fixture = JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures/hand-trick-repeat.json")))!;
        var confirm = Action("combat_confirm_selection");
        var before = fixture["before"]!.ToJsonString();
        var after = fixture["after"]!.ToJsonString();
        void Expect(bool expected, string afterJson, long version, string reason)
        {
            if (ActionPostconditions.Reached(confirm, Snap(before, 30), Snap(afterJson, version)) != expected) throw new Exception(reason);
            checks++;
        }
        Expect(true, after, 31, "same prompt, new invocation, nothing selected: the confirm was consumed");
        Expect(false, after, 30, "no newer version cannot settle");
        Expect(false, Rewrite(after, n =>
            n["hand_select"]!["selector"]!["invocation_id"] = fixture["before"]!["hand_select"]!["selector"]!["invocation_id"]!.GetValue<string>()),
            31, "same invocation is a reset animation, not progress");
        Expect(false, Rewrite(after, n => n["hand_select"]!["can_confirm"] = true), 31, "a confirmable new selection is not a fresh one");
        Expect(false, Rewrite(after, n => n["hand_select"]!["selector"]!["prompt_key"] = "OTHER"), 31, "a different prompt is not the repeated selector");
    }
    static void ShopRelicThatOpensRewardsAndSecondSelection()
    {
        const string ctx = "\"context\":{\"session_id\":\"s\",\"run_id\":\"r\",\"combat_epoch\":null}";
        string Shop(int gold) => "{\"state_type\":\"shop\"," + ctx + ",\"run\":{\"floor\":20},\"player\":{\"gold\":" + gold + "},\"shop\":{\"items\":[{\"index\":9,\"category\":\"relic\",\"price\":175,\"is_stocked\":true,\"relic_id\":\"ORRERY\"}]}}";
        string Rewards(int gold, int floor = 20) => "{\"state_type\":\"rewards\"," + ctx + ",\"run\":{\"floor\":" + floor + "},\"player\":{\"gold\":" + gold + "},\"rewards\":{\"items\":[{\"index\":0,\"type\":\"card\"}],\"can_proceed\":true}}";
        var buy = Action("shop_purchase", ("index", 9));
        if (!ActionPostconditions.Reached(buy, Snap(Shop(183), 5), Snap(Rewards(8), 6))) throw new Exception("Orrery: exact price gone and a rewards screen must settle the purchase");
        checks++;
        if (ActionPostconditions.Reached(buy, Snap(Shop(183), 5), Snap(Rewards(9), 6))) throw new Exception("gold not reduced by the price cannot settle");
        checks++;
        if (ActionPostconditions.Reached(buy, Snap(Shop(183), 5), Snap(Rewards(8), 5))) throw new Exception("no newer version cannot settle");
        checks++;
        if (ActionPostconditions.Reached(buy, Snap(Shop(183), 5), Snap(Rewards(8, 21), 6))) throw new Exception("another floor cannot settle");
        checks++;
        string Pick(string screen, bool sel = false) => "{\"state_type\":\"card_select\"," + ctx + ",\"card_select\":{\"screen_type\":\"" + screen + "\",\"cards\":[{\"index\":0,\"id\":\"A\",\"selected\":" + (sel ? "true" : "false") + "},{\"index\":1,\"id\":\"B\",\"selected\":false}]}}";
        var pick = Action("select_card", ("index", 0));
        if (!ActionPostconditions.Reached(pick, Snap(Pick("choose"), 5), Snap(Pick("NCombatPileCardSelectScreen"), 6))) throw new Exception("a pick that opens a different selection screen must settle");
        checks++;
        if (ActionPostconditions.Reached(pick, Snap(Pick("choose"), 5), Snap(Pick("NCombatPileCardSelectScreen"), 5))) throw new Exception("no newer version cannot settle");
        checks++;
        if (ActionPostconditions.Reached(pick, Snap(Pick("choose"), 5), Snap(Pick("choose"), 6))) throw new Exception("identical screen without a selection is not progress");
        checks++;
    }
    static void Main()
    {
        ShopRelicThatOpensRewardsAndSecondSelection();
        HandTrickRepeatsItsSelector();
        SlyDiscardPlaysOnDiscard();
        SlipperyBridgeLoop();
        PurchaseAndTutorialContinuations();
        EndTurnRetentionContinuation();
        NextRoundSelectorContinuation();
        SequentialDiscardConfirmation();
        EndTurnIntoBossChoice();
        ShopPurchaseSettlesByGold();
        const string combat = """{"state_type":"monster","battle":{"round":1,"turn":"player","enemies":[{"combat_id":7,"hp":10}]},"player":{"energy":3,"block":0,"hand":[{"index":0,"id":"STRIKE","instance_id":"a"},{"index":1,"id":"STRIKE","instance_id":"b"}],"potions":[{"slot":0,"id":"FIRE"}]}}""";
        Check(false, Action("play_card", ("card_index", 0), ("card_instance_id", "a")), combat,
            combat.Replace("\"round\":1", "\"round\":2"), "round change does not complete card");
        Check(true, Action("play_card", ("card_index", 0), ("card_instance_id", "a")), combat,
            combat.Replace("{\"index\":0,\"id\":\"STRIKE\",\"instance_id\":\"a\"},", ""), "played instance leaves hand");
        Check(false, Action("play_card", ("card_index", 0), ("card_instance_id", "a")), combat,
            combat.Replace("\"instance_id\":\"b\"", "\"instance_id\":\"c\""), "other duplicate card changes");
        Check(true, Action("play_card", ("card_index", 0), ("card_instance_id", "a")), combat,
            combat.Replace("\"energy\":3", "\"energy\":2"), "retained card with combat cost");
        Check(true, Action("play_card", ("card_index", 0), ("card_instance_id", "a"), ("target", "7")), combat,
            combat.Replace("\"hp\":10", "\"hp\":6"), "retained card damages target");
        Check(true, Action("play_card", ("card_index", 0), ("card_instance_id", "a")), combat,
            """{"state_type":"card_select","card_select":{"cards":[]}}""", "card opens selection");
        Check(false, Action("end_turn"), combat, combat.Replace("\"energy\":3", "\"energy\":2"), "energy alone is not turn end");
        Check(true, Action("end_turn"), combat, combat.Replace("\"round\":1", "\"round\":2"), "round advances");
        Check(true, Action("use_potion", ("slot", 0)), combat, combat.Replace("{\"slot\":0,\"id\":\"FIRE\"}", ""), "potion slot empties");
        Check(false, Action("use_potion", ("slot", 0)), combat, combat.Replace("\"energy\":3", "\"energy\":2"), "unrelated combat change does not consume potion");
        const string potionMap = """{"state_type":"map","player":{"hp":35,"potions":[{"slot":1,"id":"JUICE"},{"slot":2,"id":"FIRE"}]},"map":{}}""";
        Check(true, Action("use_potion", ("slot", 1)), potionMap,
            potionMap.Replace("{\"slot\":1,\"id\":\"JUICE\"},", ""), "noncombat potion use consumes the chosen slot");
        Check(true, Action("discard_potion", ("slot", 2)), potionMap,
            potionMap.Replace(",{\"slot\":2,\"id\":\"FIRE\"}", ""), "noncombat discard empties chosen slot");
        Check(false, Action("discard_potion", ("slot", 1)), potionMap,
            potionMap.Replace(",{\"slot\":2,\"id\":\"FIRE\"}", ""), "other slot disappearing cannot settle potion action");

        const string grid = """{"state_type":"card_select","card_select":{"preview_showing":false,"cards":[{"index":0,"id":"A","selected":false},{"index":1,"id":"B","selected":false}],"can_confirm":false}}""";
        Check(true, Action("select_card", ("index", 1)), grid, grid.Replace("\"id\":\"B\",\"selected\":false", "\"id\":\"B\",\"selected\":true"), "chosen card selected");
        Check(false, Action("select_card", ("index", 0)), grid, grid.Replace("\"id\":\"B\",\"selected\":false", "\"id\":\"B\",\"selected\":true"), "other selected card does not complete target");
        Check(true, Action("select_card", ("index", 0)), grid,
            grid.Replace("\"preview_showing\":false", "\"preview_showing\":true,\"preview_cards\":[{\"id\":\"A\"}]"), "selected card preview opens");
        Check(false, Action("select_card", ("index", 0)), grid,
            grid.Replace("\"preview_showing\":false", "\"preview_showing\":true,\"preview_cards\":[{\"id\":\"B\"}]"), "other card preview does not complete target");
        Check(false, Action("confirm_selection"), grid, grid.Replace("\"can_confirm\":false", "\"can_confirm\":true"), "confirm remains on grid");
        Check(true, Action("confirm_selection"), grid, """{"state_type":"map","map":{}}""", "confirmation exits grid");
        var preview = grid.Replace("\"preview_showing\":false", "\"preview_showing\":true");
        Check(true, Action("cancel_selection"), preview, grid, "cancel closes preview");
        Check(false, Action("cancel_selection"), grid, grid.Replace("\"can_confirm\":false", "\"can_confirm\":true"), "unrelated grid change does not cancel");
        const string bundle = """{"state_type":"bundle_select","bundle_select":{"preview_showing":false,"bundles":[{"index":0,"selected":false},{"index":1,"selected":false}]}}""";
        Check(true, Action("select_bundle", ("index", 1)), bundle, bundle.Replace("\"index\":1,\"selected\":false", "\"index\":1,\"selected\":true"), "chosen bundle selected");
        Check(false, Action("select_bundle", ("index", 0)), bundle, bundle.Replace("\"index\":1,\"selected\":false", "\"index\":1,\"selected\":true"), "other bundle selected");
        const string handSelect = """{"state_type":"hand_select","hand_select":{"cards":[{"index":0,"name":"A"}],"selected_cards":[]}}""";
        Check(true, Action("combat_select_card", ("card_index", 0)), handSelect,
            """{"state_type":"hand_select","hand_select":{"cards":[],"selected_cards":[{"index":0,"name":"A"}]}}""", "combat card moves to selection");
        Check(false, Action("combat_confirm_selection"), handSelect,
            handSelect.Replace("\"selected_cards\":[]", "\"selected_cards\":[{\"index\":0,\"name\":\"A\"}]"), "selection alone is not confirmation");

        const string sphere = """{"state_type":"crystal_sphere","crystal_sphere":{"tool":"none","cells":[{"x":1,"y":2,"is_hidden":true},{"x":2,"y":2,"is_hidden":true}]}}""";
        Check(true, Action("crystal_sphere_click_cell", ("x", 1), ("y", 2)), sphere,
            sphere.Replace("\"x\":1,\"y\":2,\"is_hidden\":true", "\"x\":1,\"y\":2,\"is_hidden\":false"), "target cell revealed");
        Check(false, Action("crystal_sphere_click_cell", ("x", 1), ("y", 2)), sphere,
            sphere.Replace("\"x\":2,\"y\":2,\"is_hidden\":true", "\"x\":2,\"y\":2,\"is_hidden\":false"), "other cell revealed");
        Check(true, Action("crystal_sphere_set_tool", ("tool", "big")), sphere, sphere.Replace("\"tool\":\"none\"", "\"tool\":\"big\""), "chosen tool active");
        Check(false, Action("imaginary"), sphere, sphere.Replace("\"tool\":\"none\"", "\"tool\":\"big\""), "unknown action");

        const string shop = """{"state_type":"shop","shop":{"inventory_open":true,"items":[{"index":0,"id":"A","is_stocked":true},{"index":1,"id":"B","is_stocked":true}]},"player":{"gold":100}}""";
        Check(true, Action("shop_purchase", ("index", 1)), shop, shop.Replace("\"id\":\"B\",\"is_stocked\":true", "\"id\":\"B\",\"is_stocked\":false"), "target stock sold");
        Check(false, Action("shop_purchase", ("index", 0)), shop, shop.Replace("\"id\":\"B\",\"is_stocked\":true", "\"id\":\"B\",\"is_stocked\":false"), "other stock sold");
        Check(true, Action("shop_purchase", ("index", 0)),
            """{"state_type":"shop","shop":{"inventory_open":true,"items":[{"index":0,"category":"card_removal","is_stocked":true}]}}""",
            grid, "card removal purchase opens its required card selection");
        Check(false, Action("shop_purchase", ("index", 0)), shop, grid,
            "ordinary shop item cannot complete through an unrelated selection screen");
        Check(true, Action("close_shop"), shop, shop.Replace("\"inventory_open\":true", "\"inventory_open\":false"), "shop closes");
        Check(true, Action("menu_select", ("option", "singleplayer")), """{"state_type":"menu","menu_screen":"main","options":["singleplayer"]}""",
            """{"state_type":"menu","menu_screen":"singleplayer","options":["standard"]}""", "menu destination changed");
        Check(false, Action("menu_select", ("option", "singleplayer")), """{"state_type":"menu","menu_screen":"main","options":["singleplayer"],"message":"a"}""",
            """{"state_type":"menu","menu_screen":"main","options":["singleplayer"],"message":"b"}""", "message change does not select menu");
        const string resumeMenu = """{"state_type":"menu","menu_screen":"main","options":["continue"]}""";
        foreach (var room in new[] { "map", "event", "monster", "elite", "boss", "rest_site", "shop", "fake_merchant", "treasure", "rewards", "card_reward", "card_select", "bundle_select", "hand_select", "relic_select", "crystal_sphere", "game_over" })
            Check(true, Action("menu_select", ("option", "continue")), resumeMenu,
                "{\"state_type\":\"" + room + "\"}", "continue may resume into supported run phase: " + room);
        Check(false, Action("menu_select", ("option", "continue")), resumeMenu,
            """{"state_type":"transition"}""", "unfinished resume cannot settle");
        Check(false, Action("menu_select", ("option", "continue")), resumeMenu,
            """{"state_type":"unsupported"}""", "unsupported resume cannot settle");
        Check(false, Action("menu_select", ("option", "quit")), resumeMenu,
            """{"state_type":"rewards"}""", "non-launch menu action cannot settle through a run phase");
        const string tutorial = """{"state_type":"menu","menu_screen":"tutorial","options":["advance"],"ui_overlay":{"instance_id":"one","content_hash":"page1"}}""";
        Check(true, Action("menu_select", ("option", "advance")), tutorial,
            tutorial.Replace("\"one\"", "\"two\""), "a second tutorial popup is distinct even when its generic menu is unchanged");
        Check(true, Action("menu_select", ("option", "advance")), tutorial,
            tutorial.Replace("page1", "page2"), "the next visible tutorial page is distinguishable");
        Check(false, Action("menu_select", ("option", "advance")), tutorial, tutorial,
            "an unchanged tutorial popup cannot claim completion");
        Check(true, Action("choose_map_node", ("index", 0)), """{"state_type":"map","run":{"floor":1},"map":{}}""",
            """{"state_type":"monster","run":{"floor":2},"battle":{}}""", "map advances room");
        Check(false, Action("choose_map_node", ("index", 0)), """{"state_type":"map","run":{"floor":1},"map":{"next_options":[{"index":0}]}}""",
            """{"state_type":"map","run":{"floor":1},"map":{"next_options":[{"index":0}],"message":"loading"}}""", "map metadata alone");
        Check(false, Action("choose_map_node", ("index", 0)), """{"state_type":"map","run":{"floor":1},"map":{}}""",
            """{"state_type":"map","run":{"floor":2},"map":{}}""", "floor advance on map is still a room transition");
        const string eventStart = """{"state_type":"event","event":{"body":"Choose","in_dialogue":false,"options":[{"index":0,"title":"Accept","actionable":true,"was_chosen":false},{"index":1,"title":"Leave","actionable":true,"was_chosen":false}]}}""";
        Check(true, Action("choose_event_option", ("index", 0)), eventStart,
            eventStart.Replace("\"title\":\"Accept\",\"actionable\":true,\"was_chosen\":false", "\"title\":\"Accept\",\"actionable\":false,\"was_chosen\":true"), "target event option chosen");
        Check(false, Action("choose_event_option", ("index", 0)), eventStart,
            eventStart.Replace("\"title\":\"Leave\",\"actionable\":true,\"was_chosen\":false", "\"title\":\"Leave\",\"actionable\":false,\"was_chosen\":true"), "other event option chosen");
        const string ancientChoices = """{"state_type":"event","event":{"event_id":"NEOW","body":null,"options":[{"index":0,"title":"New Leaf","is_proceed":false},{"index":1,"title":"Fishing Rod","is_proceed":false}]}}""";
        Check(true, Action("choose_event_option", ("index", 1)), ancientChoices,
            """{"state_type":"event","event":{"event_id":"NEOW","body":null,"options":[{"index":0,"title":"Continue","is_proceed":true}]}}""",
            "Neow reward advances to proceed even when its body remains null");
        Check(false, Action("choose_event_option", ("index", 1)), ancientChoices,
            """{"state_type":"event","event":{"event_id":"NEOW","body":null,"options":[{"index":0,"title":"Fishing Rod","is_proceed":false}]}}""",
            "removing an unchosen event option does not complete the selected one");
        Check(true, Action("advance_dialogue"), eventStart.Replace("\"in_dialogue\":false", "\"in_dialogue\":true"),
            eventStart.Replace("\"body\":\"Choose\"", "\"body\":\"Next\""), "dialogue body advances");
        Check(true, Action("advance_dialogue"), """{"state_type":"event","event":{"in_dialogue":true,"dialogue_revision":1}}""",
            """{"state_type":"event","event":{"in_dialogue":true,"dialogue_revision":2}}""", "visible ancient dialogue advances to the next line");
        const string rewards = """{"state_type":"rewards","rewards":{"items":[{"index":0,"type":"gold","description":"Gold"},{"index":1,"type":"card","description":"Card"}]}}""";
        Check(true, Action("claim_reward", ("index", 1)), rewards,
            """{"state_type":"rewards","rewards":{"items":[{"index":0,"type":"gold","description":"Gold"}]}}""", "target reward removed");
        Check(false, Action("claim_reward", ("index", 1)), rewards,
            """{"state_type":"rewards","rewards":{"items":[{"index":0,"type":"card","description":"Card"}]}}""", "other reward removed while target remains");
        const string twoGold = """{"state_type":"rewards","rewards":{"items":[{"index":0,"type":"gold","description":"15 Gold"},{"index":1,"type":"gold","description":"15 Gold"},{"index":2,"type":"card","description":"Card"}]}}""";
        Check(true, Action("claim_reward", ("index", 0)), twoGold,
            """{"state_type":"rewards","rewards":{"items":[{"index":0,"type":"gold","description":"15 Gold"},{"index":1,"type":"card","description":"Card"}]}}""",
            "one of two identical gold rewards claimed (Amethyst Aubergine)");
        Check(false, Action("claim_reward", ("index", 0)), twoGold,
            """{"state_type":"rewards","rewards":{"items":[{"index":0,"type":"gold","description":"15 Gold"},{"index":1,"type":"gold","description":"15 Gold"},{"index":2,"type":"card","description":"Card","actionable":true}]}}""",
            "two identical gold rewards both still listed");
        Check(false, Action("claim_reward", ("index", 0)), twoGold,
            """{"state_type":"rewards","rewards":{"items":[{"index":0,"type":"gold","description":"15 Gold"},{"index":1,"type":"gold","description":"15 Gold"}]}}""",
            "a different reward removed while both gold rewards remain");
        Check(true, Action("open_chest"), """{"state_type":"treasure","treasure":{"can_open_chest":true}}""",
            """{"state_type":"treasure","treasure":{"can_open_chest":false,"relics":[{"index":0,"id":"R"}]}}""", "chest exposes relic");
        Check(true, Action("select_card_reward", ("card_index", 0)),
            """{"state_type":"card_reward","card_reward":{"cards":[{"index":0,"id":"A","actionable":true}]}}""",
            """{"state_type":"map","map":{}}""", "card reward closes");
        const string cardRewardBefore = """{"state_type":"card_reward","card_reward":{"cards":[{"index":0,"id":"A"}],"can_skip":true,"can_reroll":true},"ui_overlay":{"message":"a"}}""";
        const string cardRewardChangedCards = """{"state_type":"card_reward","card_reward":{"cards":[{"index":0,"id":"B"}],"can_skip":true,"can_reroll":false},"ui_overlay":{"message":"a"}}""";
        const string cardRewardRerollConsumed = """{"state_type":"card_reward","card_reward":{"cards":[{"index":0,"id":"A"}],"can_skip":true,"can_reroll":false},"ui_overlay":{"message":"a"}}""";
        const string cardRewardSkipConsumed = """{"state_type":"card_reward","card_reward":{"cards":[{"index":0,"id":"A"}],"can_skip":false,"can_reroll":true},"ui_overlay":{"message":"a"}}""";
        const string cardRewardUiChanged = """{"state_type":"card_reward","card_reward":{"cards":[{"index":0,"id":"A"}],"can_skip":true,"can_reroll":true},"ui_overlay":{"message":"b"}}""";
        Check(true, Action("reroll_card_reward"), cardRewardBefore, cardRewardChangedCards, "changed reward cards confirm reroll");
        Check(false, Action("reroll_card_reward"), cardRewardBefore, cardRewardRerollConsumed, "reroll flag alone does not confirm reroll");
        Check(false, Action("skip_card_reward"), cardRewardBefore, cardRewardChangedCards, "rerolled cards do not confirm skip");
        Check(true, Action("skip_card_reward"), cardRewardBefore, """{"state_type":"map","map":{}}""", "leaving reward screen confirms skip");
        Check(true, Action("skip_card_reward"), cardRewardBefore, cardRewardSkipConsumed, "losing skip confirms skip");
        foreach (var action in new[] { "reroll_card_reward", "skip_card_reward" })
        {
            Check(false, Action(action), cardRewardBefore, cardRewardBefore, "unchanged reward does not confirm " + action);
            Check(false, Action(action), cardRewardBefore, cardRewardUiChanged, "unrelated UI change does not confirm " + action);
        }
        Check(true, Action("choose_rest_option", ("index", 0)),
            """{"state_type":"rest_site","rest_site":{"options":[{"index":0,"name":"Rest","is_enabled":true}],"can_proceed":false}}""",
            """{"state_type":"rest_site","rest_site":{"options":[{"index":0,"name":"Rest","is_enabled":false}],"can_proceed":true}}""", "rest choice enables proceed");
        Check(true, Action("claim_treasure_relic", ("index", 0)),
            """{"state_type":"treasure","treasure":{"relics":[{"index":0,"id":"R"}],"can_proceed":false}}""",
            """{"state_type":"treasure","treasure":{"relics":[],"can_proceed":true}}""", "treasure relic claimed");
        using (var fixture = JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures/neow-proceed.json"))))
        {
            var data = fixture.RootElement;
            Check(true, Action("choose_event_option", ("index", 1)), data.GetProperty("before").GetRawText(),
                data.GetProperty("after").GetRawText(), "the captured live Neow reward must release its pending operation");
        }
        using (var fixture = JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures/resume-rewards.json"))))
        {
            var data = fixture.RootElement;
            Check(true, Action("menu_select", ("option", "continue")), data.GetProperty("before").GetRawText(),
                data.GetProperty("after").GetRawText(), "captured continue into rewards must settle without retrying load");
        }
        Console.WriteLine($"Postcondition tests passed: {checks} assertions");
    }
}
