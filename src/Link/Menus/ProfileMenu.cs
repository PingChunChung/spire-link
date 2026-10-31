using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using MegaCrit.Sts2.Core.Saves;
using SpireLink.Actions;
using SpireLink.Game;
using SpireLink.State.Screens;

namespace SpireLink.Menus;

// The profile screen: "back", or a profile slot as "profile_N", "slot_N" or "N" (1-3). A slot is chosen by clicking
// its button, so the game runs its own switch flow (save reload, menu refresh) exactly as for a player.
internal static class ProfileMenu
{
    private const int Slots = 3;

    public static Dictionary<string, object?> Select(NProfileScreen screen, string option)
    {
        if (MenuActions.Is(option, "back"))
        {
            var back = MenuScreens.ProfileBackButton(screen);
            if (!Ui.Enabled(back)) return Outcome.Fail("Back button not available on profile select");
            back!.ForceClick();
            return Outcome.Ok("Going back from profile select");
        }

        if (ParseSlot(option) is not { } slot)
            return Outcome.Fail($"Unknown profile select option: {option}. Use: profile_1, profile_2, profile_3, back");
        var button = screen.GetNodeOrNull<NProfileButton>($"%ProfileButton{slot}");
        if (button == null || !Nodes.Visible(button)) return Outcome.Fail($"Profile {slot} button not available");
        button.ForceClick();
        return Outcome.Ok($"Switch requested for profile {slot} (via UI)",
            ("target_profile_id", slot), ("current_profile_id", SaveManager.Instance?.CurrentProfileId));
    }

    private static int? ParseSlot(string option)
    {
        var text = option.Trim().ToLowerInvariant();
        foreach (var prefix in new[] { "profile_", "slot_" })
            if (text.StartsWith(prefix, StringComparison.Ordinal)) { text = text[prefix.Length..]; break; }
        return int.TryParse(text, out var slot) && slot is >= 1 and <= Slots ? slot : null;
    }
}
