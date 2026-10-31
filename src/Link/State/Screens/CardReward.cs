using Godot;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using SpireLink.Game;

namespace SpireLink.State.Screens;

// Group C. The "card_reward" field (pick one of the offered cards).
// Contract: {cards: [Cards.Choice (index = holder position in screen order, actionable = holder clickable and the screen's
// completion source still open)], can_skip, can_reroll}. Skip and reroll are NCardRewardAlternativeButtons bound by option
// id ("Skip", "REROLL"); see CardRewardAlternatives.IndexOf for the id-to-button rule.
// The screen keeps no public handle on its pending pick or its alternatives, so both come from its private fields.
internal static class CardRewardScreen
{
    public static Dictionary<string, object?> Read(NCardRewardSelectionScreen screen)
    {
        bool open = Pending(screen);
        var holders = Holders(screen);
        var cards = new List<Dictionary<string, object?>>();
        for (int i = 0; i < holders.Count; i++)
            if (holders[i].CardModel is { } card)
                cards.Add(Cards.Choice(card, i, open && Ui.CardHolderEnabled(holders[i])));
        return new()
        {
            ["cards"] = cards,
            ["can_skip"] = Alternative(screen, CardRewardAlternatives.SkipOptionId) != null,
            ["can_reroll"] = Alternative(screen, CardRewardAlternatives.RerollOptionId) != null
        };
    }

    // The offered cards' holders in screen order; "index" here and in select_card_reward is a position in this list.
    public static List<NCardHolder> Holders(NCardRewardSelectionScreen screen) => Nodes.ByPosition(Nodes.All<NCardHolder>(screen));

    // The screen still waits for a pick: its completion source exists and has not been resolved.
    public static bool Pending(NCardRewardSelectionScreen screen)
        => Nodes.Field(screen, "_completionSource") is TaskCompletionSource<int?> { Task.IsCompleted: false };

    // The alternative button bound to `optionId` when it can be clicked, else null. RefreshOptions creates one button per
    // entry of _extraOptions in order, so the id maps to a button by index; buttons already queued for deletion by a
    // refresh are left out of the count, hidden or disabled ones stay in it.
    public static NCardRewardAlternativeButton? Alternative(NCardRewardSelectionScreen screen, string optionId)
    {
        if (!Nodes.Live(screen) || !Pending(screen)
            || Nodes.Field(screen, "_extraOptions") is not IReadOnlyList<CardRewardAlternative> options
            || Nodes.Field(screen, "_rewardAlternativesContainer") is not Control container || !Nodes.Live(container))
            return null;
        var buttons = container.GetChildren().OfType<NCardRewardAlternativeButton>().Where(b => Nodes.Live(b)).ToArray();
        int index = CardRewardAlternatives.IndexOf(options.Select(o => o.OptionId).ToArray(), buttons.Length, optionId);
        return index >= 0 && Ui.Enabled(buttons[index]) ? buttons[index] : null;
    }
}
