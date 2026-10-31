using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using SpireLink.Game;

namespace SpireLink.State.Screens;

// Group D. The "bundle_select" field (choose one bundle of cards).
// Contract: {screen_type: "bundle", prompt: "Choose a bundle.", bundles: [{index, card_count, selected, actionable,
// cards: [Cards.Choice (actionable false)]}], preview_showing, preview_cards: [Cards.Choice] (NPreviewCardHolders under
// %Cards), can_cancel, can_confirm}.
internal static class BundleSelectScreen
{
    public static Dictionary<string, object?> Read(NChooseABundleSelectionScreen screen)
    {
        var preview = screen.GetNodeOrNull<Control>("%BundlePreviewContainer");
        bool previewShown = Nodes.Visible(preview);
        var chosen = Nodes.Field(screen, "_selectedBundle");

        var bundles = new List<Dictionary<string, object?>>();
        foreach (var bundle in Nodes.All<NCardBundle>(screen))
        {
            var cards = bundle.Bundle.Select((card, i) => Cards.Choice(card, i, false)).ToList();
            bundles.Add(new()
            {
                ["index"] = bundles.Count,
                ["card_count"] = cards.Count,
                ["selected"] = ReferenceEquals(chosen, bundle),
                ["actionable"] = Nodes.Visible(bundle) && Ui.Enabled(bundle.Hitbox) && !previewShown,
                ["cards"] = cards
            });
        }

        // The preview holders exist whether or not the preview is up; they are listed either way.
        var previews = new List<Dictionary<string, object?>>();
        foreach (var holder in Nodes.All<NPreviewCardHolder>(screen.GetNodeOrNull<Control>("%Cards")))
            if (holder.CardModel is { } card) previews.Add(Cards.Choice(card, previews.Count, false));

        return new()
        {
            ["screen_type"] = "bundle",
            ["prompt"] = "Choose a bundle.",
            ["bundles"] = bundles,
            ["preview_showing"] = Nodes.Live(preview) && preview!.Visible,
            ["preview_cards"] = previews,
            ["can_cancel"] = Ui.Enabled(screen.GetNodeOrNull<NBackButton>("%Cancel")),
            ["can_confirm"] = Ui.Enabled(screen.GetNodeOrNull<NConfirmButton>("%Confirm"))
        };
    }
}
