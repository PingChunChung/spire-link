using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using SpireLink.Game;

namespace SpireLink.State.Screens;

// Group A. The "hand_select" field while the hand asks for cards ("Select a card to exhaust"); "battle" is read beside it.
// Contract: {mode ("simple_select"/"upgrade_select"/Mode name), prompt? (the %SelectionHeader text), cards: [Cards.Info +
// index (holder position in ActiveHolders, skipped holders still count), description (hand pile), selected: false,
// actionable], selected_cards?: [{index, selected: true, actionable: false, name}] (only when non-empty), can_confirm}.
internal static class HandSelectScreen
{
    public static Dictionary<string, object?> Read(NPlayerHand hand)
    {
        var state = new Dictionary<string, object?>
        {
            ["mode"] = hand.CurrentMode switch
            {
                NPlayerHand.Mode.SimpleSelect => "simple_select",
                NPlayerHand.Mode.UpgradeSelect => "upgrade_select",
                var other => other.ToString()
            }
        };
        if (hand.GetNodeOrNull<Control>("%SelectionHeader") is { } header) state["prompt"] = Text.OfLabel(header);

        var holders = hand.ActiveHolders;
        var cards = new List<Dictionary<string, object?>>();
        for (int i = 0; i < holders.Count; i++)
        {
            if (holders[i].CardModel is not { } card) continue;
            var entry = Cards.Info(card);
            entry["index"] = i;
            entry["description"] = Cards.Description(card);
            entry["selected"] = false;
            entry["actionable"] = Selectable(hand, holders[i]);
            cards.Add(entry);
        }
        state["cards"] = cards;

        if (hand.GetNodeOrNull<Control>("%SelectedHandCardContainer") is { } container)
        {
            var selected = new List<Dictionary<string, object?>>();
            foreach (var holder in Nodes.All<NSelectedHandCardHolder>(container))
            {
                if (holder.CardModel is not { } card) continue;
                selected.Add(new()
                {
                    ["index"] = selected.Count,
                    ["selected"] = true,
                    ["actionable"] = false,
                    ["name"] = Text.Of(() => card.Title)
                });
            }
            if (selected.Count > 0) state["selected_cards"] = selected;
        }

        state["can_confirm"] = Ui.Enabled(ConfirmButton(hand));
        return state;
    }

    // A card can be picked while the hand itself is asking (not peeking at a pile, nothing stacked on top), its holder
    // is clickable and the selection filter, when the prompt has one, accepts it. The "actionable" flag and the
    // combat_select_card action share this test.
    public static bool Selectable(NPlayerHand hand, NHandCardHolder holder)
    {
        if (holder.CardModel is not { } card) return false;
        if (!hand.IsInCardSelection || hand.PeekButton.IsPeeking || NOverlayStack.Instance is not { ScreenCount: 0 }) return false;
        if (!Ui.CardHolderEnabled(holder)) return false;
        return Nodes.Field(hand, "_currentSelectionFilter") is not Func<CardModel, bool> filter || filter(card);
    }

    // The hand's own confirm button, enabled once enough cards are picked.
    public static NConfirmButton? ConfirmButton(NPlayerHand hand) => hand.GetNodeOrNull<NConfirmButton>("%SelectModeConfirmButton");
}
