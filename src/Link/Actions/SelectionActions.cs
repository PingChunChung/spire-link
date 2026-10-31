using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using SpireLink.Game;
using SpireLink.State.Screens;

namespace SpireLink.Actions;

// Group G: the overlay screens that ask for a choice: a card grid (deck select/upgrade/transform, simple select), a
// choose-a-card row, a bundle row and a relic row. Every handler acts on the top overlay only, checks what the matching
// reader reports as actionable, then takes the same path a mouse click would: the holder's or grid's press signal for
// cards, ForceClick for buttons and hitboxes. Indices are positions in the reader's lists (CardSelectScreen.Holders for
// cards, tree order for bundles and relic holders).
internal static class SelectionActions
{
    private static IOverlayScreen? Top => Ui.TopOverlay;

    public static Dictionary<string, object?> SelectCard(ActionContext ctx)
    {
        var top = Top;
        if (ctx.Int("index") is not { } index) return Outcome.Fail("Missing 'index' (card index in the grid)");
        switch (top)
        {
            case NCardGridSelectionScreen grid:
            {
                if (Nodes.First<NCardGrid>(grid) is not { } cardGrid) return Outcome.Fail("Card grid not found in selection screen");
                var holders = CardSelectScreen.Holders(grid);
                if (index < 0 || index >= holders.Count) return Outcome.Fail(OutOfRange("Card", index, holders.Count, "cards"));
                var holder = holders[index];
                if (!CardSelectScreen.Pickable(grid, holder, CardSelectScreen.ShownPreviews(grid).Count > 0))
                    return Outcome.Fail($"Card index {index} is not actionable");
                // The grid relays holder presses to the screen; pressing through it keeps the grid's own bookkeeping.
                cardGrid.EmitSignal(NCardGrid.SignalName.HolderPressed, holder);
                return Outcome.Ok($"Toggling card selection: {CardName(holder)}");
            }
            case NChooseACardSelectionScreen choose:
            {
                var holders = CardSelectScreen.Holders(choose);
                if (index < 0 || index >= holders.Count) return Outcome.Fail(OutOfRange("Card", index, holders.Count, "cards"));
                var holder = holders[index];
                if (!Open(choose) || !Ui.CardHolderEnabled(holder)) return Outcome.Fail($"Card index {index} is not actionable");
                holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
                return Outcome.Ok($"Choosing card: {CardName(holder)}");
            }
            default:
                return Outcome.Fail("No card selection screen is open");
        }
    }

    // With a preview up only its own Confirm counts; otherwise the screen's Confirm, or any enabled one for the enchant
    // screens that place it elsewhere.
    public static Dictionary<string, object?> ConfirmSelection(ActionContext ctx)
    {
        var top = Top;
        if (top is NChooseACardSelectionScreen)
            return Outcome.Fail("Choose-a-card screen requires no confirmation - use select_card(index) to pick directly");
        if (top is not NCardGridSelectionScreen screen) return Outcome.Fail("No card selection screen is open");

        var previews = CardSelectScreen.ShownPreviews(screen);
        foreach (var container in previews)
            if (Click(CardSelectScreen.Button<NConfirmButton>(container, "Confirm", "%PreviewConfirm")))
                return Outcome.Ok("Confirming selection from preview");
        if (previews.Count > 0) return Outcome.Fail("No preview confirm button is currently actionable");

        if (Click(CardSelectScreen.Button<NConfirmButton>(screen, "Confirm", "%Confirm"))) return Outcome.Ok("Confirming selection");
        if (Nodes.All<NConfirmButton>(screen).Any(Click)) return Outcome.Ok("Confirming selection");
        return Outcome.Fail("No confirm button is currently enabled - select more cards first");
    }

    // Skip on a choose-a-card screen; on a grid, back out of a preview first, else close the screen.
    public static Dictionary<string, object?> CancelSelection(ActionContext ctx)
    {
        var top = Top;
        if (top is NChooseACardSelectionScreen choose)
            return Open(choose) && Click(Skip(choose))
                ? Outcome.Ok("Skipping card choice")
                : Outcome.Fail("No skip option available - a card must be chosen");
        if (top is not NCardGridSelectionScreen screen) return Outcome.Fail("No card selection screen is open");

        var previews = CardSelectScreen.ShownPreviews(screen);
        foreach (var container in previews)
            if (Click(CardSelectScreen.Button<NBackButton>(container, "Cancel", "%PreviewCancel")))
                return Outcome.Ok("Cancelling preview - returning to card selection");
        if (previews.Count > 0) return Outcome.Fail("No preview cancel button is currently actionable");

        if (Click(screen.GetNodeOrNull<NBackButton>("%Close"))) return Outcome.Ok("Closing card selection screen");
        return Outcome.Fail("No cancel/close button is currently enabled - selection may be mandatory");
    }

    public static Dictionary<string, object?> SelectBundle(ActionContext ctx)
    {
        if (Top is not NChooseABundleSelectionScreen screen) return Outcome.Fail("No bundle selection screen is open");
        if (ctx.Int("index") is not { } index) return Outcome.Fail("Missing 'index' (bundle index)");
        if (Ui.Visible(screen, "%BundlePreviewContainer"))
            return Outcome.Fail("A bundle preview is already open - confirm or cancel it first");
        var bundles = Nodes.All<NCardBundle>(screen).ToList();
        if (index < 0 || index >= bundles.Count) return Outcome.Fail(OutOfRange("Bundle", index, bundles.Count, "bundles"));
        var bundle = bundles[index];
        if (!Nodes.Visible(bundle) || !Click(bundle.Hitbox)) return Outcome.Fail($"Bundle index {index} is not actionable");
        return Outcome.Ok($"Selecting bundle {index}");
    }

    public static Dictionary<string, object?> ConfirmBundleSelection(ActionContext ctx)
    {
        if (Top is not NChooseABundleSelectionScreen screen) return Outcome.Fail("No bundle selection screen is open");
        return Click(screen.GetNodeOrNull<NConfirmButton>("%Confirm"))
            ? Outcome.Ok("Confirming bundle selection")
            : Outcome.Fail("Bundle confirm button is not enabled");
    }

    public static Dictionary<string, object?> CancelBundleSelection(ActionContext ctx)
    {
        if (Top is not NChooseABundleSelectionScreen screen) return Outcome.Fail("No bundle selection screen is open");
        return Click(screen.GetNodeOrNull<NBackButton>("%Cancel"))
            ? Outcome.Ok("Cancelling bundle selection")
            : Outcome.Fail("Bundle cancel button is not enabled");
    }

    // Index counts every holder in tree order, as RelicSelectScreen reports them.
    public static Dictionary<string, object?> SelectRelic(ActionContext ctx)
    {
        if (Top is not NChooseARelicSelection screen) return Outcome.Fail("No relic selection screen is open");
        if (ctx.Int("index") is not { } index) return Outcome.Fail("Missing 'index' (relic index)");
        var holders = Nodes.All<NRelicBasicHolder>(screen).ToList();
        if (index < 0 || index >= holders.Count) return Outcome.Fail(OutOfRange("Relic", index, holders.Count, "relics"));
        var holder = holders[index];
        if (!Open(screen) || holder.Relic?.Model is not { } relic || !Click(holder))
            return Outcome.Fail($"Relic index {index} is not actionable");
        return Outcome.Ok($"Selecting relic: {Text.Of(() => relic.Title) ?? "unknown"}");
    }

    public static Dictionary<string, object?> SkipRelicSelection(ActionContext ctx)
    {
        if (Top is not NChooseARelicSelection screen) return Outcome.Fail("No relic selection screen is open");
        return Open(screen) && Click(Skip(screen))
            ? Outcome.Ok("Skipping relic selection")
            : Outcome.Fail("No skip option available");
    }

    private static bool Open(Node screen) => Ui.ChoiceOpen(screen);

    private static NClickableControl? Skip(Node screen) => Ui.SkipButton(screen);

    // Clicks the control when it can be clicked; false otherwise, so callers can fall through to the next candidate.
    private static bool Click(NClickableControl? control)
    {
        if (!Ui.Enabled(control)) return false;
        control!.ForceClick();
        return true;
    }

    private static string CardName(NCardHolder holder) => Text.Of(() => holder.CardModel?.Title) ?? "unknown";

    private static string OutOfRange(string what, int index, int count, string plural)
        => $"{what} index {index} out of range ({count} {plural} available)";
}
