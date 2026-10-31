using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace SpireLink.Game;

// Whether a control can be clicked right now, and the {"name", "enabled"} option entries menus report.
internal static class Ui
{
    // Visible in the tree and enabled: the plain clickability test for buttons and holders.
    public static bool Enabled(NClickableControl? control) => Nodes.Visible(control) && control!.IsEnabled;

    public static Node? OpenModal
    {
        get
        {
            try { return NModalContainer.Instance?.OpenModal as Node; }
            catch (ObjectDisposedException) { return null; }
        }
    }

    public static bool Within(Node? node, Node? ancestor)
    {
        if (ancestor == null) return false;
        try
        {
            for (var current = node; Nodes.Live(current); current = current!.GetParent())
                if (ReferenceEquals(current, ancestor)) return true;
        }
        catch (ObjectDisposedException) { }
        return false;
    }

    // A control inside the open modal counts as shown even while the modal is still animating in (IsVisibleInTree is false).
    public static bool InOpenModal(NClickableControl? control, Node? modal = null)
    {
        modal ??= OpenModal;
        try { return Nodes.Live(control) && Nodes.Live(modal) && control!.Visible && Within(control, modal); }
        catch (ObjectDisposedException) { return false; }
    }

    // Visible, or inside the open modal while it animates in; enabled or not.
    public static bool Shown(NClickableControl? control) => Nodes.Visible(control) || InOpenModal(control);

    // Popup and tutorial buttons: shown and enabled.
    public static bool Clickable(NClickableControl? control)
    {
        try { return Shown(control) && control!.IsEnabled; }
        catch (ObjectDisposedException) { return false; }
    }

    public static bool MapOpen => NMapScreen.Instance is { } map && (map.IsOpen || Nodes.Visible(map));

    // The overlay on top of the stack, if any; the screen an in-run observation and the selection actions act on.
    public static IOverlayScreen? TopOverlay => NOverlayStack.Instance?.Peek();

    // The top overlay when it is a T, else null.
    public static T? Overlay<T>() where T : class => TopOverlay as T;

    // Choose-a-card and choose-a-relic screens flag themselves complete once a pick or skip went through.
    public static bool ChoiceOpen(Node screen) => Nodes.Field(screen, "_screenComplete") is false;

    public static NClickableControl? SkipButton(Node screen) => screen.GetNodeOrNull<NClickableControl>("SkipButton");

    // A card holder the player can click: it shows a card, its hitbox is enabled and the holder's own clickable flag
    // (a protected field the reward screen clears briefly after opening) is set.
    public static bool CardHolderEnabled(NCardHolder? holder)
        => holder?.CardModel != null && Nodes.Visible(holder) && Enabled(holder.Hitbox) && Nodes.Field(holder, "_isClickable") is true;

    public static bool Visible(Node? owner, string uniqueName)
        => Nodes.Live(owner) && Nodes.Visible(owner!.GetNodeOrNull<Control>(uniqueName));

    public static Dictionary<string, object?> Option(string name, bool enabled) => new() { ["name"] = name, ["enabled"] = enabled };

    // Adds the option when the owner's button field holds a visible control; enabled follows the button's IsEnabled.
    public static void AddOption(List<Dictionary<string, object?>> options, object owner, string field, string name)
    {
        if (Nodes.Field(owner, field) is not Control button || !Nodes.Visible(button)) return;
        options.Add(Option(name, button is not NClickableControl clickable || clickable.IsEnabled));
    }
}
