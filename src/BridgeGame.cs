using System.Runtime.CompilerServices;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using SpireLink.Actions;
using SpireLink.Game;
using SpireLink.State;
using SpireLink.State.Screens;

namespace SpireLink;

public static partial class SpireLinkMod
{
    private sealed record CardHandle(string Value);
    private sealed record SelectionHandle(string Value);
    private static ConditionalWeakTable<CardModel, CardHandle> _cardHandles = new();
    private static ConditionalWeakTable<object, SelectionHandle> _selectionHandles = new();
    private static object? _runReference;
    private static object? _combatReference;
    private static string? _runId;
    private static string? _combatEpoch;

    internal static bool IsMultiplayerRun()
    {
        try { return RunManager.Instance.IsInProgress && RunManager.Instance.NetService.Type.IsMultiplayer(); }
        catch { return false; }
    }

    // All callers run on the Godot main thread. Handles track live objects, never card names.
    private static Dictionary<string, object?> ReadGameState()
    {
        if (IsMultiplayerRun())
            return new() { ["state_type"] = "unsupported", ["reason"] = "multiplayer_not_supported" };
        var manager = RunManager.Instance;
        if (manager.IsInProgress && (manager.DebugOnlyGetState()?.Rng == null
            || manager.ActionQueueSet == null || manager.ActionExecutor == null))
            return new() { ["state_type"] = "transition", ["reason"] = "run_services_initializing" };
        Dictionary<string, object?>? state;
        try { state = Observer.Read(); }
        catch (Exception ex)
        {
            // Never let one screen's reader turn observe into HTTP 500: report an unsupported state with the reason and keep the log.
            Godot.GD.PrintErr($"[Spire Link] state build failed: {ex.GetType().Name}: {ex.Message} {ex.StackTrace}");
            return new() { ["state_type"] = "unsupported", ["reason"] = "state_build_failed:" + ex.GetType().Name + ": " + ex.Message };
        }
        // A reader gives up (null) when the UI it describes is not wired yet, e.g. the hand before its selection prompt exists.
        if (state == null)
            return new() { ["state_type"] = "unsupported", ["reason"] = "state_read_incomplete" };
        if (state.GetValueOrDefault("state_type") as string == "menu") AddVisibleOverlayContext(state);
        if (state.GetValueOrDefault("menu_screen") as string == "character_select")
        {
            var screen = Nodes.First<NCharacterSelectScreen>(Nodes.Root);
            if (screen?.Lobby != null)
            {
                state["selected_character_id"] = screen.Lobby.LocalPlayer.character?.Id.Entry;
                state["ascension"] = screen.Lobby.Ascension;
                state["max_ascension"] = screen.Lobby.MaxAscension;
                state["seed"] = screen.Lobby.Seed;
            }
        }
        var run = RunManager.Instance.IsInProgress ? RunManager.Instance.DebugOnlyGetState() : null;
        if (!ReferenceEquals(run, _runReference))
        {
            _runReference = run;
            _runId = run == null ? null : Guid.NewGuid().ToString("N");
            _combatReference = null;
            _combatEpoch = null;
            _cardHandles = new();
            _selectionHandles = new();
            _wasLowHp = false;
        }
        var player = run == null ? null : LocalContext.GetMe(run);
        if (run != null && state.GetValueOrDefault("run") is Dictionary<string, object?> runInfo)
            runInfo["seed"] = run.Rng.StringSeed;
        var combat = CombatManager.Instance.IsInProgress ? player?.Creature?.CombatState : null;
        if (!ReferenceEquals(combat, _combatReference))
        {
            _combatReference = combat;
            _combatEpoch = combat == null ? null : Guid.NewGuid().ToString("N");
            _cardHandles = new();
            _powerHandles = new();
            _selectionHandles = new();
        }
        state["context"] = new Dictionary<string, object?>
        {
            ["session_id"] = SessionId,
            ["run_id"] = _runId,
            ["combat_epoch"] = _combatEpoch
        };
        bool engineIdle = run == null || (RunManager.Instance.ActionQueueSet.IsEmpty
            && !RunManager.Instance.ActionExecutor.IsRunning
            && RunManager.Instance.ActionExecutor.CurrentlyRunningAction is null);
        state["engine"] = new Dictionary<string, object?> { ["action_queue_idle"] = engineIdle };
        if (state.TryGetValue("player", out var playerValue) && playerValue is Dictionary<string, object?> ps && player != null)
        {
            // Player.Deck is the persistent run deck. Combat piles are transient and
            // may contain created or exhausted cards, so never infer the deck from them.
            var deckCards = player.Deck.Cards;
            var deck = new List<Dictionary<string, object?>>(deckCards.Count);
            var byId = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var byType = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var upgradedCount = 0;
            foreach (var card in deckCards)
            {
                var entry = Cards.Info(card);
                entry["target_type"] = card.TargetType.ToString();
                entry["current_upgrade_level"] = card.CurrentUpgradeLevel;
                entry["max_upgrade_level"] = card.MaxUpgradeLevel;
                deck.Add(entry);
                var id = card.Id.Entry;
                var type = card.Type.ToString();
                byId[id] = byId.GetValueOrDefault(id) + 1;
                byType[type] = byType.GetValueOrDefault(type) + 1;
                if (card.IsUpgraded) upgradedCount++;
            }
            ps["deck"] = deck;
            ps["deck_summary"] = new Dictionary<string, object?>
            {
                ["total_cards"] = deck.Count,
                ["unique_card_ids"] = byId.Count,
                ["upgraded_cards"] = upgradedCount,
                ["by_id"] = byId,
                ["by_type"] = byType
            };
            // Match Cards.Pile's model order before sorting its public
            // inventory. The runtime draw order must remain hidden.
            var pileIssues = new List<string>();
            var combatCards = player.PlayerCombatState;
            if (combatCards != null)
            {
                var drawCards = combatCards.DrawPile.Cards.ToList();
                drawCards.Sort((c1, c2) => c1.Rarity != c2.Rarity
                    ? c1.Rarity.CompareTo(c2.Rarity)
                    : string.Compare(c1.Id.Entry, c2.Id.Entry, StringComparison.Ordinal));
                AddPileIdentity(ps, "draw_pile", drawCards, pileIssues);
                AddPileIdentity(ps, "discard_pile", combatCards.DiscardPile.Cards, pileIssues);
                AddPileIdentity(ps, "exhaust_pile", combatCards.ExhaustPile.Cards, pileIssues);
            }
            else if (ps.ContainsKey("draw_pile") || ps.ContainsKey("discard_pile")
                || ps.ContainsKey("exhaust_pile"))
                pileIssues.Add("combat_pile_models_unavailable");
            if (pileIssues.Count > 0)
            {
                ps["pile_identity_complete"] = false;
                state["metadata_issues"] = pileIssues;
            }
            else if (combatCards != null && ps.ContainsKey("draw_pile"))
                ps["pile_identity_complete"] = true;
            // Publish only a deterministic draw inventory and retain its count.
            if (ps.GetValueOrDefault("draw_pile") is List<Dictionary<string, object?>> drawPile)
            {
                ps["draw_pile"] = drawPile
                    .OrderBy(card => card.GetValueOrDefault("name") as string, StringComparer.Ordinal)
                    .ThenBy(card => card.GetValueOrDefault("cost") as string, StringComparer.Ordinal)
                    .ThenBy(card => card.GetValueOrDefault("description") as string, StringComparer.Ordinal)
                    .ThenBy(card => card.GetValueOrDefault("instance_id") as string, StringComparer.Ordinal)
                    .ToList();
                ps["draw_pile_ordering"] = "unordered_unknown_draw_order";
            }
            if (ps.TryGetValue("hand", out var handValue) && handValue is List<Dictionary<string, object?>> hand)
            {
                var cards = player.PlayerCombatState?.Hand.Cards;
                if (cards != null)
                    for (int i = 0; i < Math.Min(hand.Count, cards.Count); i++)
                        hand[i]["instance_id"] = Handle(cards[i]);
            }
            if (ps.TryGetValue("potions", out var potionValue) && potionValue is List<Dictionary<string, object?>> potions)
            {
                foreach (var entry in potions)
                {
                    int slot = Convert.ToInt32(entry["slot"]);
                    var potion = player.GetPotionAtSlotIndex(slot);
                    bool canDiscard = potion != null && !potion.IsQueued && !player.Creature.IsDead;
                    bool canUse = potion != null && canDiscard
                        && potion.PassesCustomUsabilityCheck && potion.Usage != PotionUsage.Automatic;
                    if (CombatManager.Instance.IsInProgress)
                    {
                        canUse &= Combat.IsPlayPhase(player) && !CombatManager.Instance.PlayerActionsDisabled;
                        canDiscard &= Combat.IsPlayPhase(player) && !CombatManager.Instance.PlayerActionsDisabled;
                    }
                    else
                        canUse &= potion?.Usage != PotionUsage.CombatOnly;
                    entry["can_use"] = canUse;
                    entry["can_discard"] = canDiscard;
                }
            }
        }
        if (state.TryGetValue("battle", out var battleValue) && battleValue is Dictionary<string, object?> battle)
        {
            var handUi = NCombatRoom.Instance?.Ui?.Hand;
            bool uiReady = handUi == null || (!handUi.InCardPlay && handUi.CurrentMode == NPlayerHand.Mode.Play);
            battle["can_end_turn"] = player != null && Combat.IsPlayPhase(player)
                && !CombatManager.Instance.PlayerActionsDisabled && uiReady;
            battle["actions_disabled"] = CombatManager.Instance.PlayerActionsDisabled || !uiReady || !engineIdle
                || RunManager.Instance.ActionQueueSynchronizer.CombatState.ToString() != "PlayPhase";
            if (battle["actions_disabled"] is true) battle["can_end_turn"] = false;
            if (player != null)
            {
                try { AddDecisionMechanics(state, player); }
                catch (Exception ex)
                {
                    var assembly = _mechanicsAssembly.Value;
                    state["decision_mechanics"] = BridgeMechanicsSchema.Build(_combatEpoch,
                        assembly.Build, assembly.Sha256, [], [],
                        ["mechanics_observation_read_failed:" + ex.GetType().Name]);
                }
            }
        }
        if (state.GetValueOrDefault("state_type") as string == "event"
            && state.GetValueOrDefault("event") is Dictionary<string, object?> eventState)
            AddVisibleEventText(eventState);
        if (state.GetValueOrDefault("state_type") as string == "hand_select"
            && state.GetValueOrDefault("hand_select") is Dictionary<string, object?> handSelect)
            AddHandSelectionHandles(handSelect);
        return state;
    }

    private static void AddVisibleOverlayContext(Dictionary<string, object?> state)
    {
        var root = Nodes.Root;
        Node? overlay = (state.GetValueOrDefault("menu_screen") as string) switch
        {
            "tutorial" => FtueScreens.VisibleTutorial(root),
            "tutorial_prompt" => FtueScreens.VisibleTutorialPrompt(root),
            "popup" => FtueScreens.VisiblePopup(root) ?? Ui.OpenModal,
            _ => null
        };
        if (overlay == null) return;
        var text = Nodes.All<RichTextLabel>(overlay).Where(Nodes.Visible).Select(ReadVisibleRichText)
            .Concat(Nodes.All<Label>(overlay).Where(Nodes.Visible).Select(ReadVisibleLabel))
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Take(30).ToList();
        state["ui_overlay"] = new Dictionary<string, object?>
        {
            ["instance_id"] = overlay.GetInstanceId().ToString(),
            ["type"] = overlay.GetType().Name,
            ["visible_text"] = text,
            ["content_hash"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(text)))).ToLowerInvariant()
        };
    }

    private static void AddVisibleEventText(Dictionary<string, object?> eventState)
    {
        var uiRoom = NEventRoom.Instance;
        var layout = uiRoom?.Layout;
        if (layout == null || !Nodes.Visible(layout)) return;
        if (Nodes.Field(layout, "_description") is RichTextLabel description
            && Nodes.Visible(description))
        {
            var body = ReadVisibleRichText(description);
            if (!string.IsNullOrWhiteSpace(body)) eventState["body"] = body;
        }

        if (eventState.GetValueOrDefault("in_dialogue") is not true
            || layout is not NAncientEventLayout ancientLayout) return;
        if (Nodes.Field(ancientLayout, "_currentDialogueLine") is int revision)
            eventState["dialogue_revision"] = revision;

        // These are instantiated UI rows, not the model's full dialogue list.
        // Older rows can remain on screen; the last visible row is the current one.
        var visibleLines = Nodes.All<NAncientDialogueLine>(ancientLayout)
            .Where(Nodes.Visible).ToList();
        if (visibleLines.Count == 0) return;
        var currentLine = visibleLines[^1];
        var text = Nodes.All<RichTextLabel>(currentLine)
            .Where(Nodes.Visible).Select(ReadVisibleRichText)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(text))
            text = Nodes.All<Label>(currentLine).Where(Nodes.Visible)
                .Select(ReadVisibleLabel)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .OrderByDescending(value => value!.Length).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(text))
        {
            eventState["dialogue_text"] = text;
            eventState["dialogue_text_scope"] = "current_ui_line_full_text";
        }
    }

    private static string? ReadVisibleLabel(Label label)
    {
        try
        {
            // Read the current message, not its per-frame typewriter animation.
            // Future dialogue nodes are excluded by the visible-node selection.
            return Text.Plain(label.Text).Trim();
        }
        catch (ObjectDisposedException) { return null; }
    }

    private static string? ReadVisibleRichText(RichTextLabel label)
    {
        try
        {
            var text = label.GetParsedText();
            if (string.IsNullOrWhiteSpace(text)) text = Text.Plain(label.Text);
            return text.Trim();
        }
        catch (ObjectDisposedException) { return null; }
    }

    private static void AddHandSelectionHandles(Dictionary<string, object?> handSelect)
    {
        var hand = NPlayerHand.Instance;
        if (hand == null)
        {
            handSelect["selector"] = new Dictionary<string, object?> { ["invocation_id"] = null };
            handSelect["metadata_issue"] = "selection_ui_unavailable";
            return;
        }
        // Preserve the actual selector contract. Localized header text alone cannot
        // identify which end-turn hook is waiting for input.
        var completion = Nodes.Field(hand, "_selectionCompletionSource")
            as TaskCompletionSource<IEnumerable<CardModel>>;
        var invocationId = completion != null && !completion.Task.IsCompleted
            ? _selectionHandles.GetValue(completion,
                _ => new SelectionHandle($"{SessionId}:selection-{Guid.NewGuid():N}")).Value
            : null;
        handSelect["selector"] = new Dictionary<string, object?> { ["invocation_id"] = invocationId };
        if (invocationId == null)
            handSelect["metadata_issue"] = "selection_completion_source_unavailable_or_completed";
        if (Nodes.Field(hand, "_prefs") is MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs prefs
            && prefs.Prompt is { } prompt)
        {
            var filter = Nodes.Field(hand, "_currentSelectionFilter") as Func<CardModel, bool>;
            handSelect["selector"] = new Dictionary<string, object?>
            {
                ["invocation_id"] = invocationId,
                ["prompt_table"] = prompt.LocTable,
                ["prompt_key"] = prompt.LocEntryKey,
                ["min_select"] = prefs.MinSelect,
                ["max_select"] = prefs.MaxSelect,
                ["require_manual_confirmation"] = prefs.RequireManualConfirmation,
                ["cancelable"] = prefs.Cancelable,
                ["filter_target_model_id"] = (filter?.Target as AbstractModel)?.Id.Entry
            };
        }
        if (handSelect.GetValueOrDefault("cards") is List<Dictionary<string, object?>> cards)
        {
            var holders = hand.ActiveHolders;
            foreach (var entry in cards)
            {
                var index = Convert.ToInt32(entry.GetValueOrDefault("index"));
                if (index >= 0 && index < holders.Count && holders[index].CardModel is { } card)
                    entry["instance_id"] = Handle(card);
            }
        }
        if (handSelect.GetValueOrDefault("selected_cards") is not List<Dictionary<string, object?>> selected)
            return;
        var selectedContainer = hand.GetNodeOrNull<Control>("%SelectedHandCardContainer");
        if (selectedContainer == null) return;
        var selectedHolders = Nodes.All<NSelectedHandCardHolder>(selectedContainer)
            .Where(holder => holder.CardModel != null).ToList();
        var selectedInstances = new List<string>();
        foreach (var entry in selected)
        {
            var index = Convert.ToInt32(entry.GetValueOrDefault("index"));
            if (index < 0 || index >= selectedHolders.Count) continue;
            var instanceId = Handle(selectedHolders[index].CardModel!);
            entry["instance_id"] = instanceId;
            selectedInstances.Add(instanceId);
        }
        handSelect["selected_instances"] = selectedInstances;
    }

    // Random opaque handles keep identical draw-pile cards from revealing their
    // runtime order through sequential IDs, including when sorting ties occur.
    private static string Handle(CardModel card) => _cardHandles.GetValue(card,
        _ => new CardHandle($"{_combatEpoch ?? "preview"}:card-{Guid.NewGuid():N}")).Value;

    private static void AddPileIdentity(Dictionary<string, object?> playerState, string key,
        IEnumerable<CardModel> source, List<string> issues)
    {
        if (playerState.GetValueOrDefault(key) is not List<Dictionary<string, object?>> entries)
        {
            issues.Add($"{key}_identity_incomplete");
            return;
        }
        var cards = source.ToList();
        if (entries.Count != cards.Count)
        {
            issues.Add($"{key}_identity_incomplete");
            return;
        }
        var identities = new List<(string? Id, string InstanceId)>(cards.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in cards)
        {
            var id = card.Id.Entry;
            var instanceId = Handle(card);
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(instanceId))
            {
                issues.Add($"{key}_identity_incomplete");
                return;
            }
            identities.Add((id, instanceId));
        }
        for (int i = 0; i < entries.Count; i++)
        {
            entries[i]["id"] = identities[i].Id;
            entries[i]["instance_id"] = identities[i].InstanceId;
        }
    }

    private static bool InteractionReady(Dictionary<string, object?> state)
    {
        string type = state.GetValueOrDefault("state_type") as string ?? "unknown";
        if (type is "unsupported" or "unknown" or "overlay") return false;
        // The skip button appears before the reward cards finish becoming clickable.
        // Do not present that temporary partial catalog as a decision-ready screen.
        if (type == "card_reward")
            return state.GetValueOrDefault("card_reward") is Dictionary<string, object?> reward
                && reward.GetValueOrDefault("cards") is List<Dictionary<string, object?>> cards
                && cards.Any(card => card.GetValueOrDefault("actionable") is true);
        if (state.GetValueOrDefault("engine") is Dictionary<string, object?> engine
            && engine.GetValueOrDefault("action_queue_idle") is false
            && type is not ("hand_select" or "card_select" or "bundle_select" or "relic_select")) return false;
        if (type is "monster" or "elite" or "boss")
            return state.GetValueOrDefault("battle") is Dictionary<string, object?> battle
                && battle.GetValueOrDefault("is_play_phase") is true
                && battle.GetValueOrDefault("actions_disabled") is not true;
        return true;
    }

    private static object ExecuteAgentAction(AgentAction action)
    {
        if (action.Action is "open_shop" or "close_shop" or "open_chest")
            return ExecuteExplicitUiAction(action.Action);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(action.Arguments, _jsonOptions))!;
        if (action.Action == "play_card" && arguments.TryGetValue("card_instance_id", out var handle))
        {
            var run = RunManager.Instance.DebugOnlyGetState();
            var player = run == null ? null : LocalContext.GetMe(run);
            var cards = player?.PlayerCombatState?.Hand.Cards;
            int index = -1;
            if (cards != null)
                for (int i = 0; i < cards.Count; i++)
                    if (Handle(cards[i]) == handle.GetString()) { index = i; break; }
            if (index < 0) return Outcome.Fail("Card instance is no longer in hand");
            arguments["card_index"] = JsonSerializer.SerializeToElement(index);
        }
        if (action.Action == "menu_select")
        {
            string option = arguments.GetValueOrDefault("option").GetString() ?? "";
            if (option is "continue" or "confirm" or "embark") _runObservationOrigin = option == "continue" ? "resumed" : "started";
            string? seed = arguments.TryGetValue("seed", out var seedArg) ? seedArg.GetString() : null;
            if (option is "confirm" or "embark")
            {
                var queued = ApplyQueuedRunSpec();
                if (queued is Dictionary<string, object?> failure) return failure;
                seed ??= queued as string;
            }
            return MenuActions.Select(option, seed);
        }
        return SpireLink.Actions.Dispatcher.Execute(action.Action, arguments);
    }

    // An operator may drop SpireLink.next_run.cfg beside the DLL: {"ascension": 0, "seed": "ABC"}.
    // It is consumed by the next embark so batch tooling can pick difficulty and fixed seeds.
    // Returns the seed (or null), or an error dictionary when the spec cannot be applied.
    private static object? ApplyQueuedRunSpec()
    {
        string directory = Path.GetDirectoryName(typeof(SpireLinkMod).Assembly.Location)!;
        string path = Path.Combine(directory, NextRunFileName);
        if (!File.Exists(path)) return null;
        JsonElement spec;
        try { spec = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path)); }
        catch (Exception ex) { return Outcome.Fail($"Invalid next_run spec: {ex.Message}"); }
        var screen = Nodes.First<NCharacterSelectScreen>(Nodes.Root);
        if (screen?.Lobby == null) return Outcome.Fail("Character select lobby is not available for next_run spec");
        if (spec.TryGetProperty("ascension", out var ascension) && ascension.ValueKind == JsonValueKind.Number)
        {
            int level = ascension.GetInt32();
            if (level < 0 || level > screen.Lobby.MaxAscension)
                return Outcome.Fail($"Requested ascension {level} exceeds unlocked maximum {screen.Lobby.MaxAscension}");
            if (Nodes.Field(screen, "_ascensionPanel") is not NAscensionPanel panel)
                return Outcome.Fail("Ascension panel is not available");
            panel.SetAscensionLevel(level);
            if (screen.Lobby.Ascension != level) return Outcome.Fail("Ascension change was not applied by the lobby");
        }
        string? seed = spec.TryGetProperty("seed", out var seedValue) && seedValue.ValueKind == JsonValueKind.String
            ? seedValue.GetString() : null;
        File.Delete(path);
        return string.IsNullOrWhiteSpace(seed) ? null : seed;
    }

    private static object ExecuteExplicitUiAction(string action)
    {
        var root = Nodes.Root;
        if (action == "open_chest")
        {
            var chest = Nodes.First<NTreasureRoom>(root)?.GetNodeOrNull<NClickableControl>("Chest");
            if (chest is not { IsEnabled: true, Visible: true }) return Outcome.Fail("Chest cannot be opened");
            chest.ForceClick();
        }
        else if (NMerchantRoom.Instance is { } merchant)
        {
            if (action == "open_shop")
            {
                if (merchant.Inventory == null || merchant.Inventory.IsOpen) return Outcome.Fail("Shop cannot be opened");
                merchant.OpenInventory();
            }
            else
            {
                var back = Nodes.First<NBackButton>(merchant);
                if (back is not { IsEnabled: true }) return Outcome.Fail("Shop cannot be closed");
                back.ForceClick();
            }
        }
        else if (Nodes.First<NFakeMerchant>(root) is { } fake)
        {
            if (action == "open_shop")
            {
                if (fake.MerchantButton is not { IsEnabled: true, Visible: true }) return Outcome.Fail("Shop cannot be opened");
                fake.MerchantButton.ForceClick();
            }
            else
            {
                var back = Nodes.First<NBackButton>(fake);
                if (back is not { IsEnabled: true }) return Outcome.Fail("Shop cannot be closed");
                back.ForceClick();
            }
        }
        else return Outcome.Fail("No shop is visible");
        return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = action };
    }

    /// <summary>animation_mode "keep" leaves the player's own setting. Any other value pins SaveManager.Instance.PrefsSave.FastMode. The save manager does not exist yet
    /// while the mod initializes, so this returns false until it does and the caller retries every frame; an unknown mode name throws.</summary>
    internal static bool TryApplyAnimationMode(string mode)
    {
        if (mode == "keep") return true;
        if (!Enum.TryParse<FastModeType>(mode, true, out var parsed) || !Enum.IsDefined(parsed))
            throw new ArgumentException("animation_mode must be keep or one of: " + string.Join(", ", Enum.GetNames<FastModeType>()));
        var prefs = SaveManager.Instance?.PrefsSave;
        if (prefs == null) return false;
        if (prefs.FastMode != parsed) prefs.FastMode = parsed;
        return true;
    }
}
