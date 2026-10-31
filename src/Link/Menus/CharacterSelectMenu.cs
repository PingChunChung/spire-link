using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using SpireLink.Actions;
using SpireLink.Game;

namespace SpireLink.Menus;

// Character select, singleplayer only: a character by id or title, "confirm"/"embark" (with an optional seed), or "back".
internal static class CharacterSelectMenu
{
    public static Dictionary<string, object?> Select(NCharacterSelectScreen screen, string option, string? seed)
    {
        if (MenuActions.Is(option, "back")) return MenuActions.ClickField(screen, "_backButton", "Going back", "Back button not available");
        if (MenuActions.Is(option, "confirm") || MenuActions.Is(option, "embark")) return Embark(screen, seed);

        // Only the characters the state offers (a hidden button is not an option there).
        var buttons = Nodes.All<NCharacterSelectButton>(screen).Select(b => (Button: b, Character: CharacterOf(b)))
            .Where(b => b.Character != null && Nodes.Visible(b.Button)).ToList();
        foreach (var (button, character) in buttons)
        {
            if (!MenuActions.Is(option, character!.Id.Entry) && !MenuActions.Is(option, Text.Of(() => character.Title) ?? "")) continue;
            if (button.IsLocked) return Outcome.Fail($"Character '{option}' is locked");
            button.Select();
            return Outcome.Ok($"Selected {Text.Of(() => character.Title)}. Use 'confirm' to embark.");
        }
        var available = buttons.Where(b => !b.Button.IsLocked).Select(b => b.Character!.Id.Entry);
        return Outcome.Fail($"Character '{option}' not found. Available: {string.Join(", ", available)}");
    }

    private static Dictionary<string, object?> Embark(NCharacterSelectScreen screen, string? seed)
    {
        if (!string.IsNullOrWhiteSpace(seed))
        {
            seed = seed.Trim();
            if (screen.Lobby == null)
                return Outcome.Fail("Seeded embark is not supported for standard singleplayer from this API. Seed was not applied and the run was not started.");
            try { screen.Lobby.SetSeed(seed); }
            catch (Exception ex)
            {
                // In standard mode the lobby stores the seed and then its listener throws ("Seed should not be changed in
                // standard mode"); the stored seed is what the run start reads, so a stored match is a successful set.
                if (screen.Lobby.Seed != seed) return Outcome.Fail($"Seeded embark failed before starting the run: {ex.Message}");
            }
        }

        if (Nodes.Field(screen, "_embarkButton") is not NClickableControl embark || !embark.IsEnabled)
            return Outcome.Fail("Embark button not available — select a character first");
        embark.ForceClick();
        return Outcome.Ok(string.IsNullOrEmpty(seed) ? "Embarking on run" : $"Embarking on run (seed: {seed})");
    }

    // A button whose character fails to load is skipped.
    private static CharacterModel? CharacterOf(NCharacterSelectButton button)
    {
        try { return button.Character; }
        catch { return null; }
    }
}
