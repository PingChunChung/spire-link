using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Ftue;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using SpireLink.Game;

namespace SpireLink.State.Screens;

// Group D. Tutorials and popups that cover whatever is underneath, checked before anything else.
// TryRead always returns true; state is null (nothing covering) or one of:
// "tutorial_prompt" {state_type: "menu", menu_screen, message: "Enable Tutorials? Choose yes or no.", options: no/yes},
// "tutorial" {..., message: "Tutorial popup active. Use advance to dismiss.", options: advance/proceed (enabled when an
// advance button is found)}, "popup" {..., message: "Popup active.", body: null, options: [{name, enabled}] from the vertical
// popup's yes/no buttons or any visible popup buttons}. The popup's own title and body are private properties the old
// reader never reached, so the message stays the constant; the bridge's ui_overlay context carries the visible text.
// The node finders feed that context (BridgeGame.AddVisibleOverlayContext) and the menu actions.
internal static class FtueScreens
{
    private static readonly string FtueNamespace = typeof(NFtue).Namespace + ".";

    // The game's tutorials keep their dismiss button in one of these; any other clickable field, then any clickable
    // descendant, is the fallback.
    private static readonly string[] AdvanceFields = { "_confirmButton", "_nextButton" };

    // Where a button keeps its caption, when it is not a yes/no button.
    private static readonly string[] CaptionFields = { "_label", "_textLabel", "_title", "_buttonLabel" };

    public static bool TryRead(Node? root, out Dictionary<string, object?>? state)
    {
        state = root == null ? null : Covering(root);
        return true;
    }

    private static Dictionary<string, object?>? Covering(Node root)
    {
        if (VisibleTutorialPrompt(root) != null)
            return Menu("tutorial_prompt", "Enable Tutorials? Choose yes or no.", Ui.Option("no", true), Ui.Option("yes", true));
        if (VisibleTutorial(root) is { } tutorial)
        {
            bool canAdvance = AdvanceButton(tutorial) != null;
            return Menu("tutorial", "Tutorial popup active. Use advance to dismiss.", Ui.Option("advance", canAdvance), Ui.Option("proceed", canAdvance));
        }
        var popup = VisiblePopup(root);
        var buttons = popup != null ? PopupOptions(popup) : PopupButtonOptions(root);
        if (buttons.Count == 0) return null;
        var state = Menu("popup", "Popup active.", buttons.Select(b => Ui.Option(b.Name, b.Button.IsEnabled)).ToArray());
        state["body"] = null;
        return state;
    }

    private static Dictionary<string, object?> Menu(string screen, string message, params Dictionary<string, object?>[] options) => new()
    {
        ["state_type"] = "menu",
        ["menu_screen"] = screen,
        ["message"] = message,
        ["options"] = options.ToList()
    };

    // The active "Enable Tutorials?" prompt, or null.
    public static NAcceptTutorialsFtue? VisibleTutorialPrompt(Node? root)
    {
        if (Ui.OpenModal is NAcceptTutorialsFtue open && Active(open)) return open;
        return Nodes.All<NAcceptTutorialsFtue>(root).FirstOrDefault(Active);
    }

    // Any other active node from the game's Ftue namespace, or null: inside the open modal first, then anywhere.
    public static Node? VisibleTutorial(Node? root)
        => Nodes.All<Node>(Ui.OpenModal).FirstOrDefault(IsTutorial) ?? Nodes.All<Node>(root).FirstOrDefault(IsTutorial);

    private static bool IsTutorial(Node node)
        => node is not NAcceptTutorialsFtue && (node.GetType().FullName ?? "").StartsWith(FtueNamespace, StringComparison.Ordinal) && Active(node);

    // A tutorial counts as active when it is visible, is the open modal, or its confirm button shows.
    private static bool Active(Node node)
    {
        if (node is not CanvasItem canvas || !Nodes.Live(node)) return false;
        if (Nodes.Visible(canvas) || ReferenceEquals(Ui.OpenModal, node)) return true;
        try { return Nodes.Field(node, "_confirmButton") is NClickableControl confirm && Ui.Shown(confirm); }
        catch (ObjectDisposedException) { return false; }
    }

    // The clickable button that dismisses or pages a tutorial, or null.
    public static NClickableControl? AdvanceButton(Node tutorial)
    {
        foreach (var name in AdvanceFields)
            if (Nodes.Field(tutorial, name) is NClickableControl button && Ui.Clickable(button)) return button;
        try
        {
            foreach (var field in tutorial.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (field.GetValue(tutorial) is NClickableControl button && Ui.Clickable(button)) return button;
        }
        catch (ObjectDisposedException) { return null; }
        return Nodes.All<NClickableControl>(tutorial).FirstOrDefault(Ui.Clickable);
    }

    // The vertical popup that offers yes/no, or null: the open modal itself, one inside it, or any visible one.
    public static NVerticalPopup? VisiblePopup(Node? root)
    {
        var modal = Ui.OpenModal;
        if (modal is NVerticalPopup open && PopupOptions(open).Count > 0) return open;
        foreach (var popup in Nodes.All<NVerticalPopup>(modal))
            if (PopupOptions(popup).Count > 0) return popup;
        foreach (var popup in Nodes.All<NVerticalPopup>(root))
            if ((Nodes.Visible(popup) || Ui.Within(popup, modal)) && PopupOptions(popup).Count > 0) return popup;
        return null;
    }

    // A vertical popup's clickable yes and no buttons, named by their caption.
    public static List<(string Name, NClickableControl Button)> PopupOptions(NVerticalPopup popup)
    {
        var options = new List<(string, NClickableControl)>();
        foreach (var (button, fallback) in new[] { (popup.YesButton, "yes"), (popup.NoButton, "no") })
            if (Ui.Clickable(button)) options.Add((Text.OptionName(Caption(button)) ?? fallback, button));
        return options;
    }

    // Without a vertical popup: every shown button in the open modal, every yes/no button anywhere, then every button in
    // a visible modal container, one entry per distinct name.
    public static List<(string Name, NClickableControl Button)> PopupButtonOptions(Node root)
    {
        var options = new List<(string Name, NClickableControl Button)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var button in Nodes.All<NClickableControl>(Ui.OpenModal)) Add(options, seen, button, null);
        foreach (var button in Nodes.All<NPopupYesNoButton>(root)) Add(options, seen, button, button.IsYes ? "yes" : "no");
        foreach (var container in Nodes.All<NModalContainer>(root))
            if (Nodes.Visible(container))
                foreach (var button in Nodes.All<NClickableControl>(container)) Add(options, seen, button, null);
        return options;
    }

    private static void Add(List<(string Name, NClickableControl Button)> options, HashSet<string> seen, NClickableControl button, string? fallback)
    {
        if (!Ui.Shown(button)) return;
        var name = Text.OptionName(Caption(button)) ?? fallback;
        if (!string.IsNullOrWhiteSpace(name) && seen.Add(name)) options.Add((name, button));
    }

    // A button's caption, tags included: its label field, else the first text under it, else its node name without "Button".
    private static string? Caption(NClickableControl button)
    {
        if (button is NPopupYesNoButton) return RawText(Nodes.Field(button, "_label") as Node);
        foreach (var field in CaptionFields)
            if (RawText(Nodes.Field(button, field) as Node) is { } caption) return caption;
        foreach (var node in Nodes.All<Node>(button))
            if (RawText(node) is { } caption) return caption;
        var name = button.Name.ToString();
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name.EndsWith("Button", StringComparison.OrdinalIgnoreCase) ? name[..^"Button".Length] : name;
    }

    // The node's "text" property as it is; null when absent or blank.
    private static string? RawText(Node? node)
    {
        if (!Nodes.Live(node)) return null;
        var text = node!.Get("text");
        if (text.VariantType == Variant.Type.Nil) return null;
        var value = text.AsString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
