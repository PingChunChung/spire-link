using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using SpireLink.Game;

namespace SpireLink.State.Screens;

// Group D. The "card_select" field, from either a grid screen (deck transform/upgrade/select, simple select) or a
// choose-a-card screen.
// Contract (ReadGrid): {screen_type ("transform"/"upgrade"/"select"/"simple_select"/type name), prompt? (%BottomLabel),
// cards: [Cards.Choice + selected] (NGridCardHolders in screen order), preview_showing, preview_cards? (Cards.AddPreviews
// over the upgrade/enchant/generic preview containers, only while a preview shows), can_cancel, can_confirm}.
// Contract (ReadChoice): {screen_type: "choose", prompt: "Choose a card.", cards: [Cards.Choice], can_skip,
// preview_showing: false, can_confirm: false, can_cancel (= can_skip)}.
internal static class CardSelectScreen
{
    private const string UpgradeSingle = "%UpgradeSinglePreviewContainer";
    private const string UpgradeMulti = "%UpgradeMultiPreviewContainer";
    private const string Generic = "%PreviewContainer";

    // Any of these showing means the selection is complete and awaits confirm or cancel; the enchant containers carry
    // buttons but their cards are not listed.
    private static readonly string[] PreviewContainers =
        { UpgradeSingle, UpgradeMulti, Generic, "%EnchantSinglePreviewContainer", "%EnchantMultiPreviewContainer" };
    private static readonly string[] PreviewCardContainers = { UpgradeSingle, UpgradeMulti, Generic };

    // The card holders of a grid or choose-a-card screen in screen order; the "index" an action receives is a position in
    // this list, so readers and actions share it.
    public static List<NGridCardHolder> Holders(Node screen) => Nodes.ByPosition(Nodes.All<NGridCardHolder>(screen));

    public static List<Control> ShownPreviews(NCardGridSelectionScreen screen)
        => PreviewContainers.Select(name => screen.GetNodeOrNull<Control>(name)).Where(Nodes.Visible).ToList();

    // A card can be clicked while the screen still accepts picks: no preview up, and either this card is already
    // picked (to unpick it) or the limit is not reached.
    public static bool Pickable(NCardGridSelectionScreen screen, NGridCardHolder holder, bool preview)
    {
        if (preview || holder.CardModel is not { } card || !Ui.CardHolderEnabled(holder)) return false;
        var picked = Nodes.Field(screen, "_selectedCards") as HashSet<CardModel>;
        int limit = Nodes.Field(screen, "_prefs") is CardSelectorPrefs prefs ? prefs.MaxSelect : 0;
        return picked != null && limit > 0 && (picked.Contains(card) || picked.Count < limit);
    }

    public static Dictionary<string, object?> ReadGrid(NCardGridSelectionScreen screen)
    {
        var state = new Dictionary<string, object?> { ["screen_type"] = ScreenType(screen) };
        if (screen.GetNodeOrNull("%BottomLabel") is { } label) state["prompt"] = Text.OfLabel(label);

        var shown = ShownPreviews(screen);
        bool preview = shown.Count > 0;

        var picked = Nodes.Field(screen, "_selectedCards") as HashSet<CardModel>;
        var holders = Holders(screen);
        var cards = new List<Dictionary<string, object?>>();
        for (int i = 0; i < holders.Count; i++)
        {
            if (holders[i].CardModel is not { } card) continue;
            var entry = Cards.Choice(card, i, Pickable(screen, holders[i], preview));
            entry["selected"] = picked != null && picked.Contains(card);
            cards.Add(entry);
        }
        state["cards"] = cards;

        state["preview_showing"] = preview;
        if (preview)
        {
            var previews = new List<Dictionary<string, object?>>();
            foreach (var name in PreviewCardContainers) Cards.AddPreviews(screen.GetNodeOrNull<Control>(name), previews);
            state["preview_cards"] = previews;
        }

        // With a preview up, cancel and confirm live inside the preview container; otherwise on the screen itself
        // (%Close, and a Confirm that enchant screens place elsewhere in the tree).
        state["can_cancel"] = preview
            ? shown.Any(container => Ui.Enabled(Button<NBackButton>(container, "Cancel", "%PreviewCancel")))
            : Ui.Enabled(screen.GetNodeOrNull<NBackButton>("%Close"));
        state["can_confirm"] = preview
            ? shown.Any(container => Ui.Enabled(Button<NConfirmButton>(container, "Confirm", "%PreviewConfirm")))
            : Ui.Enabled(Button<NConfirmButton>(screen, "Confirm", "%Confirm")) || Nodes.All<NConfirmButton>(screen).Any(Ui.Enabled);
        return state;
    }

    public static Dictionary<string, object?> ReadChoice(NChooseACardSelectionScreen screen)
    {
        bool open = Ui.ChoiceOpen(screen);
        var holders = Holders(screen);
        var cards = new List<Dictionary<string, object?>>();
        for (int i = 0; i < holders.Count; i++)
            if (holders[i].CardModel is { } card) cards.Add(Cards.Choice(card, i, open && Ui.CardHolderEnabled(holders[i])));
        bool canSkip = open && Ui.Enabled(Ui.SkipButton(screen));
        return new()
        {
            ["screen_type"] = "choose",
            ["prompt"] = "Choose a card.",
            ["cards"] = cards,
            ["can_skip"] = canSkip,
            ["preview_showing"] = false,
            ["can_confirm"] = false,
            ["can_cancel"] = canSkip
        };
    }

    private static string ScreenType(NCardGridSelectionScreen screen) => screen switch
    {
        NDeckTransformSelectScreen => "transform",
        NDeckUpgradeSelectScreen => "upgrade",
        NDeckCardSelectScreen => "select",
        NSimpleCardSelectScreen => "simple_select",
        _ => screen.GetType().Name
    };

    // The button under `owner` at the first path that holds one of type T.
    public static T? Button<T>(Node? owner, string path, string alternative) where T : NClickableControl
        => owner?.GetNodeOrNull<T>(path) ?? owner?.GetNodeOrNull<T>(alternative);
}
