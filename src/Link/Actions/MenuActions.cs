using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using SpireLink.Game;
using SpireLink.Menus;
using SpireLink.State.Screens;

namespace SpireLink.Actions;

// Group H: menu_select on every out-of-run screen, game over, tutorials and popups. Screens are checked in the same
// order the old code used (whatever covers the rest wins): game over, the tutorial prompt, any other tutorial, a popup,
// the timeline, the multiplayer screens, character select, profiles, then the main menu and its submenus. Every option
// goes through the game's own button so the game runs its usual flow; option names match what the state readers report.
internal static class MenuActions
{
    // Option name to the private button field that holds it, in the main menu and the single-player submenu.
    private static readonly (string Option, string Field)[] MainButtons =
    {
        ("singleplayer", "_singleplayerButton"), ("multiplayer", "_multiplayerButton"), ("compendium", "_compendiumButton"),
        ("timeline", "_timelineButton"), ("settings", "_settingsButton"), ("continue", "_continueButton"),
        ("abandon_run", "_abandonRunButton"), ("quit", "_quitButton")
    };

    private static readonly (string Option, string Field)[] ModeButtons =
        { ("standard", "_standardButton"), ("daily", "_dailyButton"), ("custom", "_customButton") };

    // A tutorial or popup that must be answered with menu_select before gameplay actions.
    public static bool BlockingPopupVisible()
        => Nodes.Root is { } root && FtueScreens.TryRead(root, out var covering) && covering != null;

    public static Dictionary<string, object?> Select(string option, string? seed)
    {
        option = option.Trim();
        if (option.Length == 0) return Outcome.Fail("Missing menu option");
        if (Nodes.Root is not { } root) return Outcome.Fail("Cannot access scene tree");

        // The overlay stack's top entry first, so a hidden or preloaded game-over node cannot capture menu navigation.
        var gameOver = Ui.Overlay<NGameOverScreen>() ?? Nodes.All<NGameOverScreen>(root).FirstOrDefault(Nodes.Visible);
        if (gameOver != null) return GameOverMenu.Select(gameOver, option);

        if (FtueScreens.VisibleTutorialPrompt(root) is { } prompt) return TutorialPrompt(prompt, option);
        if (FtueScreens.VisibleTutorial(root) is { } tutorial) return Tutorial(tutorial, option);

        if (FtueScreens.VisiblePopup(root) is { } popup) return Popup(FtueScreens.PopupOptions(popup), option);
        var popupButtons = FtueScreens.PopupButtonOptions(root);
        if (popupButtons.Count > 0) return Popup(popupButtons, option);

        if (Nodes.Shown<NTimelineScreen>(root) is { } timeline) return TimelineMenu.Select(timeline, root, option);

        if (Nodes.Shown<NJoinFriendScreen>(root) is { } join) return Multiplayer(join, "join screen", "Going back from join screen", option);
        if (Nodes.Shown<NMultiplayerLoadGameScreen>(root) is { } lobby) return Multiplayer(lobby, "load lobby", "Going back from load lobby", option);

        // Character select can outlive the main menu node, so it comes before the main-menu routing.
        if (Nodes.Shown<NCharacterSelectScreen>(root) is { } select) return CharacterSelectMenu.Select(select, option, seed);
        if (Nodes.Shown<NProfileScreen>(root) is { } profiles) return ProfileMenu.Select(profiles, option);

        if (Nodes.First<NMainMenu>(root) is { } menu)
        {
            if (Nodes.Shown<NSingleplayerSubmenu>(root) is { } single) return Singleplayer(single, option);
            if (Nodes.Shown<NMultiplayerHostSubmenu>(root) is { } host) return Multiplayer(host, "multiplayer host", "Going back", option);
            if (Nodes.Shown<NMultiplayerSubmenu>(root) is { } multi) return Multiplayer(multi, "multiplayer", "Going back", option);
            return Main(menu, option);
        }

        return Outcome.Fail("Not on a menu screen");
    }

    // The "Enable Tutorials?" prompt: yes or no on its vertical popup.
    private static Dictionary<string, object?> TutorialPrompt(Node prompt, string option)
    {
        bool yes = Is(option, "yes");
        if (!yes && !Is(option, "no")) return Outcome.Fail($"Unknown tutorial prompt option: {option}. Use: yes, no");
        if (Nodes.Field(prompt, "_verticalPopup") is NVerticalPopup popup && (yes ? popup.YesButton : popup.NoButton) is { } button && Ui.Clickable(button))
        {
            button.ForceClick();
            return Outcome.Ok($"Tutorials: {(yes ? "enabled" : "disabled")}");
        }
        return Outcome.Fail("Tutorial popup visible but buttons not accessible");
    }

    private static Dictionary<string, object?> Tutorial(Node tutorial, string option)
    {
        if (!Is(option, "advance") && !Is(option, "proceed")) return Outcome.Fail($"Unknown tutorial option: {option}. Use: advance, proceed");
        if (FtueScreens.AdvanceButton(tutorial) is not { } button)
            return Outcome.Fail("Tutorial popup visible but no actionable advance/proceed control is available; retry after the next state poll");
        button.ForceClick();
        return Outcome.Ok("Dismissed tutorial popup");
    }

    // A popup option is clicked by the name the reader reported for it, or by its role (yes/no) when it has one.
    private static Dictionary<string, object?> Popup(List<(string Name, NClickableControl Button, string? Role)> options, string option)
    {
        foreach (var (name, button, role) in options)
        {
            if (!Is(option, name) && !(role != null && Is(option, role))) continue;
            if (!Ui.Clickable(button)) return Outcome.Fail($"Popup option '{name}' is disabled");
            button.ForceClick();
            return Outcome.Ok($"Selected popup option: {name}");
        }
        return Outcome.Fail($"Popup option '{option}' is not actionable. Use: {string.Join(", ", options.Select(o => o.Name))}");
    }

    private static Dictionary<string, object?> Singleplayer(NSingleplayerSubmenu menu, string option)
    {
        if (Is(option, "back")) return ClickField(menu, "_backButton", "Going back", "Back button is not available");
        return FieldFor(ModeButtons, option) is { } field
            ? ClickField(menu, field, $"Selected {option}", $"Option '{option}' is not available (locked)")
            : Outcome.Fail($"Unknown singleplayer option: {option}. Use: standard, daily, custom, back");
    }

    // Multiplayer is not supported: every multiplayer screen offers only the way back.
    private static Dictionary<string, object?> Multiplayer(Node screen, string label, string backMessage, string option)
        => Is(option, "back")
            ? ClickField(screen, "_backButton", backMessage, "Back button is not available")
            : Outcome.Fail($"Unknown {label} option: {option}. Multiplayer is not supported. Use: back");

    private static Dictionary<string, object?> Main(NMainMenu menu, string option)
    {
        // The timeline logs invalid unlock-state errors when opened by automation while an obtained epoch awaits its reveal.
        if (Is(option, "timeline") && MenuScreens.PendingEpochIds() is { Count: > 0 } pending)
            return Outcome.Fail("Timeline has obtained epochs that still need to be revealed manually; not opening Timeline because this game state logs invalid unlock-state errors when entered through automation",
                ("pending_epoch_ids", pending), ("manual_action_required", true));
        return FieldFor(MainButtons, option) is { } field
            ? ClickField(menu, field, $"Selected {option}", $"Option '{option}' is not available")
            : Outcome.Fail($"Unknown menu option: {option}");
    }

    private static string? FieldFor((string Option, string Field)[] buttons, string option)
        => buttons.FirstOrDefault(b => Is(option, b.Option)).Field;

    // Clicks the button held in the owner's private field when it is visible and enabled.
    internal static Dictionary<string, object?> ClickField(object owner, string field, string done, string? unavailable = null)
    {
        if (Nodes.Field(owner, field) is not NClickableControl button) return Outcome.Fail($"Could not find button '{field}'");
        if (!Ui.Enabled(button)) return Outcome.Fail(unavailable ?? $"Option '{field}' is not available");
        button.ForceClick();
        return Outcome.Ok(done);
    }

    internal static bool Is(string option, string name) => string.Equals(option, name, StringComparison.OrdinalIgnoreCase);
}
