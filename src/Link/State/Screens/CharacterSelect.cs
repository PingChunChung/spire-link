using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using SpireLink.Game;

namespace SpireLink.State.Screens;

// Group D. The character select screen, as a complete observation: {state_type: "menu", menu_screen: "character_select",
// message: "Select a character.", characters?: [{name, id, locked, hp, gold, energy, description, starting_relics?: [{name,
// description}], starting_deck?: [name...], total_cards?, total_relics?, total_potions?}], options?: [{name (character id
// or confirm/embark/back), enabled}]}. Reached from the menu, from a run with no state yet and from a run with no room;
// singleplayer only (no lobby block, no unready option).
internal static class CharacterSelectScreen
{
    public static Dictionary<string, object?> Read(NCharacterSelectScreen screen)
    {
        var state = new Dictionary<string, object?>
        {
            ["state_type"] = "menu",
            ["menu_screen"] = "character_select",
            ["message"] = "Select a character."
        };

        var characters = new List<Dictionary<string, object?>>();
        var options = new List<Dictionary<string, object?>>();
        foreach (var button in Nodes.All<NCharacterSelectButton>(screen))
        {
            CharacterModel? character;
            try { character = button.Character; }
            catch { continue; }
            if (character == null || !Nodes.Visible(button)) continue;
            options.Add(Ui.Option(character.Id.Entry, !button.IsLocked));
            try { characters.Add(Describe(character, button.IsLocked)); }
            catch { /* a character whose pools fail to load is still offered as an option */ }
        }
        if (characters.Count > 0) state["characters"] = characters;

        // The one embark button answers to both names.
        if (Nodes.Field(screen, "_embarkButton") is NClickableControl embark && Nodes.Visible(embark))
        {
            options.Add(Ui.Option("confirm", embark.IsEnabled));
            options.Add(Ui.Option("embark", embark.IsEnabled));
        }
        if (Nodes.Field(screen, "_backButton") is NClickableControl back && Nodes.Visible(back))
            options.Add(Ui.Option("back", back.IsEnabled));
        if (options.Count > 0) state["options"] = options;
        return state;
    }

    private static Dictionary<string, object?> Describe(CharacterModel character, bool locked)
    {
        var entry = new Dictionary<string, object?>
        {
            ["name"] = Text.Of(() => character.Title),
            ["id"] = character.Id.Entry,
            ["locked"] = locked,
            ["hp"] = character.StartingHp,
            ["gold"] = character.StartingGold,
            ["energy"] = character.MaxEnergy,
            ["description"] = Text.Of(() => character.CardsModifierDescription)
        };
        var relics = character.StartingRelics.Select(relic => new Dictionary<string, object?>
        {
            ["name"] = Text.Of(() => relic.Title),
            ["description"] = Text.Of(() => relic.DynamicDescription)
        }).ToList();
        if (relics.Count > 0) entry["starting_relics"] = relics;
        var deck = character.StartingDeck.Select(card => Text.Of(() => card.Title) ?? "?").ToList();
        if (deck.Count > 0) entry["starting_deck"] = deck;
        try { if (character.CardPool?.AllCards is { } cards) entry["total_cards"] = cards.Count(); } catch { }
        try { if (character.RelicPool?.AllRelics is { } pool) entry["total_relics"] = pool.Count(); } catch { }
        try { if (character.PotionPool?.AllPotions is { } potions) entry["total_potions"] = potions.Count(); } catch { }
        return entry;
    }
}
