using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using MegaCrit.Sts2.Core.Saves;
using SpireLink.Game;

namespace SpireLink.State.Screens;

// Group D. The whole observation while no run is in progress: {state_type: "menu", menu_screen, message, ...}.
// Screens, in detection order: "singleplayer" {options: [{name, enabled}] for standard/daily/custom/back},
// "multiplayer_host", "multiplayer", "multiplayer_join", "multiplayer_load_lobby" (deliberately reduced: menu_screen,
// message and options: [back] only, no lobby or friend details), "character_select" (CharacterSelectScreen.Read),
// "timeline" {options: advance/back, epochs: [{id, name, state, obtained}], total_slots, completed_count, revealed_count,
// obtained_unrevealed_count, locked_count, no_slot_count}, "compendium", "settings", "profile_select" {current_profile_id,
// options: profile_N.../back}, and "main" {options: [string...] (continue, abandon_run, singleplayer, multiplayer,
// compendium, timeline, settings, quit: enabled and visible buttons), blocked_options?: [{name: "timeline", enabled: false,
// reason: "manual_epoch_reveal_required", pending_epoch_ids}]}. Without a scene root: {state_type: "menu", message:
// "No run in progress."}.
internal static class MenuScreens
{
    private const string MultiplayerMessage = "Multiplayer is not supported. Use back to return.";

    // Button field and the option name it reports, in option order.
    private static readonly (string Field, string Name)[] ModeButtons =
        { ("_standardButton", "standard"), ("_dailyButton", "daily"), ("_customButton", "custom") };

    private static readonly (string Field, string Name)[] MainButtons =
    {
        ("_continueButton", "continue"), ("_abandonRunButton", "abandon_run"), ("_singleplayerButton", "singleplayer"),
        ("_multiplayerButton", "multiplayer"), ("_compendiumButton", "compendium"), ("_timelineButton", "timeline"),
        ("_settingsButton", "settings"), ("_quitButton", "quit")
    };

    public static Dictionary<string, object?> Read(Node? root)
    {
        var state = new Dictionary<string, object?> { ["state_type"] = "menu" };
        if (root == null)
        {
            state["message"] = "No run in progress.";
            return state;
        }

        if (Nodes.Shown<NSingleplayerSubmenu>(root) is { } single) Singleplayer(state, single);
        else if (Nodes.Shown<NMultiplayerHostSubmenu>(root) is { } host) Multiplayer(state, "multiplayer_host", host);
        else if (Nodes.Shown<NMultiplayerSubmenu>(root) is { } multi) Multiplayer(state, "multiplayer", multi);
        else if (Nodes.Shown<NJoinFriendScreen>(root) is { } join) Multiplayer(state, "multiplayer_join", join);
        else if (Nodes.Shown<NMultiplayerLoadGameScreen>(root) is { } lobby) Multiplayer(state, "multiplayer_load_lobby", lobby);
        else if (Nodes.Shown<NCharacterSelectScreen>(root) is { } select) return CharacterSelectScreen.Read(select);
        else if (Nodes.Shown<NTimelineScreen>(root) != null) Timeline(state);
        else if (Nodes.Shown<NCompendiumSubmenu>(root) != null) Name(state, "compendium", "Compendium screen.");
        else if (Nodes.Shown<NSettingsScreen>(root) != null) Name(state, "settings", "Settings screen.");
        else if (Nodes.Shown<NProfileScreen>(root) is { } profiles) ProfileSelect(state, profiles);
        else Main(state, root);
        return state;
    }

    private static void Name(Dictionary<string, object?> state, string screen, string message)
    {
        state["menu_screen"] = screen;
        state["message"] = message;
    }

    private static void Singleplayer(Dictionary<string, object?> state, NSingleplayerSubmenu menu)
    {
        Name(state, "singleplayer", "Select game mode.");
        var options = new List<Dictionary<string, object?>>();
        foreach (var (field, name) in ModeButtons) Ui.AddOption(options, menu, field, name);
        Ui.AddOption(options, menu, "_backButton", "back");
        state["options"] = options;
    }

    // The mod runs singleplayer only: a multiplayer screen offers nothing but the way back.
    private static void Multiplayer(Dictionary<string, object?> state, string screen, Node menu)
    {
        Name(state, screen, MultiplayerMessage);
        var options = new List<Dictionary<string, object?>>();
        Ui.AddOption(options, menu, "_backButton", "back");
        state["options"] = options;
    }

    private static void Timeline(Dictionary<string, object?> state)
    {
        Name(state, "timeline", "Timeline screen.");
        state["options"] = new List<Dictionary<string, object?>> { Ui.Option("advance", true), Ui.Option("back", true) };

        // Epochs come from the saved progress, not from the hover-dependent slots on screen.
        List<Dictionary<string, object?>> epochs;
        int revealed = 0, obtained = 0, locked = 0, noSlot = 0;
        try
        {
            if (SaveManager.Instance?.Progress is not { } progress) return;
            epochs = new List<Dictionary<string, object?>>();
            foreach (var epoch in progress.Epochs)
            {
                switch (epoch.State)
                {
                    case EpochState.Revealed: revealed++; break;
                    case EpochState.Obtained or EpochState.ObtainedNoSlot: obtained++; break;
                    case EpochState.NotObtained: locked++; break;
                    case EpochState.NoSlot: noSlot++; break;
                }
                epochs.Add(new()
                {
                    ["id"] = epoch.Id,
                    ["name"] = EpochName(epoch.Id),
                    ["state"] = epoch.State.ToString(),
                    ["obtained"] = epoch.ObtainDate
                });
            }
        }
        catch { return; }
        state["epochs"] = epochs;
        state["total_slots"] = epochs.Count;
        state["completed_count"] = revealed;
        state["revealed_count"] = revealed;
        state["obtained_unrevealed_count"] = obtained;
        state["locked_count"] = locked;
        state["no_slot_count"] = noSlot;
    }

    // "AncientEra3" reads as "Ancient Era": trailing digits dropped, a space before each capital that follows a lower-case letter.
    private static string EpochName(string id)
    {
        int end = id.Length;
        while (end > 0 && char.IsDigit(id[end - 1])) end--;
        var sb = new StringBuilder(end + 4);
        for (int i = 0; i < end; i++)
        {
            if (i > 0 && id[i] is >= 'A' and <= 'Z' && id[i - 1] is >= 'a' and <= 'z') sb.Append(' ');
            sb.Append(id[i]);
        }
        return sb.ToString();
    }

    private static void ProfileSelect(Dictionary<string, object?> state, NProfileScreen screen)
    {
        Name(state, "profile_select", "Profile select screen.");
        state["current_profile_id"] = CurrentProfileId();

        var options = new List<Dictionary<string, object?>>();
        if (Nodes.Field(screen, "_profileButtons") is IEnumerable<NProfileButton> buttons)
            foreach (var button in buttons)
                if (Nodes.Field(button, "_profileId") is int id) options.Add(Ui.Option($"profile_{id}", button.IsEnabled));
        if (ProfileBackButton(screen) is { } back && Nodes.Visible(back)) options.Add(Ui.Option("back", back.IsEnabled));
        if (options.Count > 0) state["options"] = options;
    }

    // The profile screen's back button: its field when the build has one, else the first back button under it.
    public static NClickableControl? ProfileBackButton(NProfileScreen screen)
        => Nodes.Field(screen, "_backButton") as NClickableControl ?? Nodes.First<NBackButton>(screen);

    // Null before the save manager has a profile (the getter throws until then).
    private static int? CurrentProfileId()
    {
        try { return SaveManager.Instance?.CurrentProfileId; }
        catch { return null; }
    }

    private static void Main(Dictionary<string, object?> state, Node root)
    {
        Name(state, "main", "Main menu.");
        if (Nodes.First<NMainMenu>(root) is not { } menu) return;

        // An epoch obtained but not yet revealed on the timeline blocks the timeline button until the player reveals it by hand.
        var pending = PendingEpochIds();
        var options = new List<string>();
        var blocked = new List<Dictionary<string, object?>>();
        foreach (var (field, name) in MainButtons)
        {
            if (Nodes.Field(menu, field) is not NClickableControl button || !Ui.Enabled(button)) continue;
            if (name == "timeline" && pending.Count > 0)
            {
                blocked.Add(new()
                {
                    ["name"] = name,
                    ["enabled"] = false,
                    ["reason"] = "manual_epoch_reveal_required",
                    ["pending_epoch_ids"] = pending
                });
                continue;
            }
            options.Add(name);
        }
        if (options.Count > 0) state["options"] = options;
        if (blocked.Count > 0) state["blocked_options"] = blocked;
    }

    // Epochs obtained but not yet revealed on the timeline; the player must reveal them by hand. The main menu's
    // blocked_options and the timeline actions share it.
    public static List<string> PendingEpochIds()
    {
        try
        {
            return SaveManager.Instance?.Progress?.Epochs
                .Where(epoch => epoch.State is EpochState.Obtained or EpochState.ObtainedNoSlot)
                .Select(epoch => epoch.Id).ToList() ?? new List<string>();
        }
        catch { return new List<string>(); }
    }
}
