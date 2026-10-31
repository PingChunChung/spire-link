using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using SpireLink.Game;
using SpireLink.State.Screens;

namespace SpireLink.Actions;

// Group E: card play, potions, end turn and the in-combat hand selection ("Select a card to exhaust").
// A card or potion is queued on the game's action queue and the turn is ended through PlayerCmd, exactly as the
// UI does it, so animation and synchronisation stay the game's own; the hand selection goes through the holder
// signal and the confirm button the UI would click.
internal static class CombatActions
{
    private const string NotInCombat = "Not in combat";
    private const string NotPlayPhase = "Not in play phase - cannot act during enemy turn";
    private const string ActionsDisabled = "Player actions are currently disabled";
    private const string NoHandSelection = "No in-combat card selection is active";

    // play_card {card_index, target?}: the card at that hand position, aimed at an enemy when it needs one.
    public static Dictionary<string, object?> PlayCard(ActionContext ctx)
    {
        var player = ctx.Player;
        if (!Combat.InProgress) return Outcome.Fail(NotInCombat);
        if (!Combat.IsPlayPhase(player)) return Outcome.Fail(NotPlayPhase);
        if (CombatManager.Instance.PlayerActionsDisabled) return Outcome.Fail(ActionsDisabled);
        if (!player.Creature.IsAlive) return Outcome.Fail("Player creature is dead - cannot play cards");
        if (player.Creature.CombatState is not { } combat) return Outcome.Fail("No combat state");

        var (index, indexError) = Index(ctx, "card_index", "Missing 'card_index'");
        if (indexError != null) return Outcome.Fail(indexError);
        if (player.PlayerCombatState?.Hand is not { } hand) return Outcome.Fail("No hand available");
        if (index < 0 || index >= hand.Cards.Count)
            return Outcome.Fail($"card_index {index} out of range (hand has {hand.Cards.Count} cards)");

        var card = hand.Cards[index];
        if (!card.CanPlay(out var reason, out _)) return Outcome.Fail($"Card '{card.Title}' cannot be played: {reason}");

        Creature? target = null;
        if (card.TargetType == TargetType.AnyEnemy)
        {
            if (!ctx.Has("target")) return Outcome.Fail("Card requires a target. Provide 'target' with an entity_id.");
            string id = ctx.String("target") ?? "";
            target = FindEnemy(combat, id);
            if (target == null) return Outcome.Fail($"Target '{id}' not found among alive enemies");
        }

        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));
        string suffix = target == null ? "" : $" targeting {Text.Of(() => target.Monster?.Title) ?? "target"}";
        return Outcome.Ok($"Playing '{card.Title}'{suffix}");
    }

    // end_turn: refused while a card is still resolving or the hand is asking for a selection, as the game's own
    // end-turn button is.
    public static Dictionary<string, object?> EndTurn(ActionContext ctx)
    {
        if (!Combat.InProgress) return Outcome.Fail(NotInCombat);
        if (!Combat.IsPlayPhase(ctx.Player)) return Outcome.Fail(NotPlayPhase);
        if (CombatManager.Instance.PlayerActionsDisabled) return Outcome.Fail(ActionsDisabled + " (turn may already be ending)");
        if (NPlayerHand.Instance is { } hand && (hand.InCardPlay || hand.CurrentMode != NPlayerHand.Mode.Play))
            return Outcome.Fail("Cannot end turn while a card is being played or hand is in selection mode");

        PlayerCmd.EndTurn(ctx.Player, canBackOut: false);
        return Outcome.Ok("Ending turn");
    }

    // use_potion {slot, target?}: combat-only potions need the play phase, automatic ones cannot be used by hand,
    // an enemy-targeted one needs `target`; self, ally and player potions go on the player's own creature.
    public static Dictionary<string, object?> UsePotion(ActionContext ctx)
    {
        var player = ctx.Player;
        var (slot, slotError) = Slot(ctx, player);
        if (slotError != null) return Outcome.Fail(slotError);
        var potion = player.GetPotionAtSlotIndex(slot);
        if (potion == null) return Outcome.Fail($"No potion in slot {slot}");

        string? title = Text.Of(() => potion.Title);
        if (potion.IsQueued) return Outcome.Fail($"Potion '{title}' is already queued for use");
        if (potion.Owner.Creature.IsDead) return Outcome.Fail("Cannot use potion - player creature is dead");
        if (!potion.PassesCustomUsabilityCheck) return Outcome.Fail($"Potion '{title}' cannot be used right now");

        bool fighting = Combat.InProgress;
        switch (potion.Usage)
        {
            case PotionUsage.CombatOnly when !fighting:
                return Outcome.Fail($"Potion '{title}' can only be used in combat");
            case PotionUsage.CombatOnly when !Combat.IsPlayPhase(player):
                return Outcome.Fail("Cannot use potions outside of play phase");
            case PotionUsage.Automatic:
                return Outcome.Fail($"Potion '{title}' is automatic and cannot be manually used");
        }
        if (fighting && CombatManager.Instance.PlayerActionsDisabled) return Outcome.Fail(ActionsDisabled);

        Creature? target = null;
        string aimed = "";
        switch (potion.TargetType)
        {
            case TargetType.AnyEnemy:
                if (!ctx.Has("target")) return Outcome.Fail("Potion requires a target enemy. Provide 'target' with an entity_id.");
                string id = ctx.String("target") ?? "";
                if (player.Creature.CombatState is not { } combat) return Outcome.Fail("No combat state for target resolution");
                target = FindEnemy(combat, id);
                if (target == null) return Outcome.Fail($"Target '{id}' not found among alive enemies");
                aimed = $" targeting {Text.Of(() => target.Monster?.Title) ?? "enemy"}";
                break;
            case TargetType.Self or TargetType.AnyAlly or TargetType.AnyPlayer:
                target = player.Creature;
                aimed = " on self";
                break;
        }

        potion.EnqueueManualUse(target);
        return Outcome.Ok($"Using potion '{title}' from slot {slot}{aimed}");
    }

    // discard_potion {slot}: allowed anywhere, like the potion belt's discard button.
    public static Dictionary<string, object?> DiscardPotion(ActionContext ctx)
    {
        var (slot, slotError) = Slot(ctx, ctx.Player);
        if (slotError != null) return Outcome.Fail(slotError);
        var potion = ctx.Player.GetPotionAtSlotIndex(slot);
        if (potion == null) return Outcome.Fail($"No potion in slot {slot}");

        string name = Text.Of(() => potion.Title) ?? "unknown";
        _ = PotionCmd.Discard(potion);
        return Outcome.Ok($"Discarded potion '{name}' from slot {slot}");
    }

    // combat_select_card {card_index}: presses the hand holder at that position (the index the hand_select state
    // reports) while the hand is asking for cards.
    public static Dictionary<string, object?> SelectHandCard(ActionContext ctx)
    {
        if (NPlayerHand.Instance is not { IsInCardSelection: true } hand) return Outcome.Fail(NoHandSelection);
        var (index, indexError) = Index(ctx, "card_index", "Missing 'card_index' (index of the card in hand)");
        if (indexError != null) return Outcome.Fail(indexError);
        var holders = hand.ActiveHolders;
        if (index < 0 || index >= holders.Count)
            return Outcome.Fail($"Card index {index} out of range ({holders.Count} selectable cards)");

        var holder = holders[index];
        if (!HandSelectScreen.Selectable(hand, holder)) return Outcome.Fail($"Card index {index} is not actionable");
        string name = Text.Of(() => holder.CardModel?.Title) ?? "unknown";

        holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
        return Outcome.Ok($"Selecting card from hand: {name}");
    }

    // combat_confirm_selection: the hand's own confirm button, enabled once enough cards are picked.
    public static Dictionary<string, object?> ConfirmHandSelection(ActionContext ctx)
    {
        if (NPlayerHand.Instance is not { IsInCardSelection: true } hand) return Outcome.Fail(NoHandSelection);
        var confirm = HandSelectScreen.ConfirmButton(hand);
        if (!Ui.Enabled(confirm)) return Outcome.Fail("Confirm button is not enabled - select more cards first");

        confirm!.ForceClick();
        return Outcome.Ok("Confirming combat card selection");
    }

    // A target named as a combat_id ("3") or as the entity_id the battle state reports ("jaw_worm_1"): the living
    // enemies are numbered the way BattleScreen numbers its "enemies" list (Combat.EntityId, in fight order). A
    // combat_id resolves to any creature in the fight.
    private static Creature? FindEnemy(ICombatState combat, string id)
    {
        if (uint.TryParse(id, out uint combatId)) return combat.GetCreature(combatId);
        var counts = new Dictionary<string, int>();
        foreach (var creature in combat.Enemies)
        {
            if (!creature.IsAlive) continue;
            if (Combat.EntityId(creature, counts) == id) return creature;
        }
        return null;
    }

    // `slot` as a potion slot index of this player.
    private static (int Slot, string? Error) Slot(ActionContext ctx, Player player)
    {
        var (slot, error) = Index(ctx, "slot", "Missing 'slot' (potion slot index)");
        if (error != null) return (slot, error);
        int slots = player.PotionSlots.Count;
        return slot < 0 || slot >= slots ? (slot, $"Potion slot {slot} out of range (player has {slots} slots)") : (slot, null);
    }

    // An integer argument; `missing` is the error when it is absent, a value of another type gets its own.
    private static (int Value, string? Error) Index(ActionContext ctx, string name, string missing)
    {
        if (!ctx.Has(name)) return (0, missing);
        return ctx.Int(name) is { } value ? (value, null) : (0, $"'{name}' must be an integer");
    }
}
