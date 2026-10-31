using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace SpireLink.Game;

// The "run" and "player" fields every in-run state carries.
internal static class Players
{
    public static Dictionary<string, object?> Run(RunState run) => new()
    {
        ["act"] = run.CurrentActIndex + 1,
        ["floor"] = run.TotalFloor,
        ["ascension"] = run.AscensionLevel
    };

    // Character, HP, block, gold, powers, relics and potions on every screen; energy, stars, hand, piles, orbs, pets and
    // combat counters only while a fight is running (PlayerCombatState lingers after combat and its energy getters run
    // hooks that need a live fight).
    public static Dictionary<string, object?> State(Player player)
    {
        var creature = player.Creature;
        var state = new Dictionary<string, object?>
        {
            ["character"] = Text.Of(() => player.Character.Title),
            ["hp"] = creature.CurrentHp,
            ["max_hp"] = creature.MaxHp,
            ["block"] = creature.Block
        };
        bool fighting = Combat.InProgress;
        if (fighting && player.PlayerCombatState is { } combat)
        {
            state["energy"] = combat.Energy;
            state["max_energy"] = combat.MaxEnergy;
            if (player.Character.ShouldAlwaysShowStarCounter || combat.Stars > 0) state["stars"] = combat.Stars;
            state["hand"] = combat.Hand.Cards.Select(Cards.InHand).ToList();
            state["draw_pile_count"] = combat.DrawPile.Cards.Count;
            state["discard_pile_count"] = combat.DiscardPile.Cards.Count;
            state["exhaust_pile_count"] = combat.ExhaustPile.Cards.Count;
            state["draw_pile"] = Cards.Pile(DrawOrderHidden(combat.DrawPile.Cards), PileType.Draw);
            state["discard_pile"] = Cards.Pile(combat.DiscardPile.Cards, PileType.Discard);
            state["exhaust_pile"] = Cards.Pile(combat.ExhaustPile.Cards, PileType.Exhaust);
            if (combat.OrbQueue is { Capacity: > 0 } orbs)
            {
                state["orbs"] = orbs.Orbs.Select(Orb).ToList();
                state["orb_slots"] = orbs.Capacity;
                state["orb_empty_slots"] = orbs.Capacity - orbs.Orbs.Count;
            }
            var pets = Powers.Pets(player);
            if (pets.Count > 0) state["pets"] = pets;
        }
        state["gold"] = player.Gold;
        try
        {
            if (fighting && creature.CombatState is { } combatState)
            {
                int played = CombatManager.Instance.History.CardPlaysStarted
                    .Count(e => e.HappenedThisTurn(combatState) && e.CardPlay.Card.Owner == player);
                state["combat_counters"] = new Dictionary<string, object?> { ["cards_played_this_turn"] = played };
            }
        }
        catch { }
        state["status"] = Powers.Of(creature);
        state["relics"] = player.Relics.Select(Relic).ToList();
        state["potions"] = player.PotionSlots.Select((potion, slot) => potion == null ? null : Potion(potion, slot))
            .Where(p => p != null).ToList();
        state["max_potion_slots"] = player.MaxPotionCount;
        return state;
    }

    // The draw pile in the order the game displays it (rarity, then id): the real draw order stays hidden.
    public static List<CardModel> DrawOrderHidden(IEnumerable<CardModel> cards)
        => cards.OrderBy(c => c.Rarity).ThenBy(c => c.Id.Entry, StringComparer.Ordinal).ToList();

    public static Dictionary<string, object?> Relic(RelicModel relic)
    {
        var entry = new Dictionary<string, object?>
        {
            ["id"] = relic.Id.Entry,
            ["name"] = Text.Of(() => relic.Title),
            ["description"] = Text.Of(() => relic.DynamicDescription),
            ["counter"] = relic.ShowCounter ? relic.DisplayAmount : null,
            ["keywords"] = Tips.Of(relic.HoverTipsExcludingRelic)
        };
        try { if (PrivateData.Of(relic, typeof(RelicModel)) is { } data) entry["private_state"] = data; } catch { }
        return entry;
    }

    public static Dictionary<string, object?> Potion(PotionModel potion, int slot) => new()
    {
        ["id"] = potion.Id.Entry,
        ["name"] = Text.Of(() => potion.Title),
        ["description"] = Text.Of(() => potion.DynamicDescription),
        ["slot"] = slot,
        ["can_use_in_combat"] = potion.Usage is PotionUsage.CombatOnly or PotionUsage.AnyTime,
        ["target_type"] = potion.TargetType.ToString(),
        ["keywords"] = Tips.Of(potion.ExtraHoverTips)
    };

    // The description with the Focus-modified passive and evoke values filled in, as the orb's own hover tip shows them.
    private static Dictionary<string, object?> Orb(OrbModel orb) => new()
    {
        ["id"] = orb.Id.Entry,
        ["name"] = Text.Of(() => orb.Title),
        ["description"] = Text.Of(() =>
        {
            var text = orb.SmartDescription;
            text.Add("energyPrefix", orb.Owner.Character.CardPool.Title);
            text.Add("Passive", orb.PassiveVal);
            text.Add("Evoke", orb.EvokeVal);
            return text;
        }),
        ["passive_val"] = orb.PassiveVal,
        ["evoke_val"] = orb.EvokeVal,
        ["keywords"] = Tips.Of(orb.HoverTips)
    };
}
