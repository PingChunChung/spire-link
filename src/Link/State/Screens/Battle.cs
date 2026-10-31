using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SpireLink.Game;

namespace SpireLink.State.Screens;

// Group A. The "battle" field of monster/elite/boss and hand_select states.
// Contract: {round, turn ("player"/"enemy", lower case of CurrentSide), is_play_phase, enemies: [Enemy...],
// dead_enemies?: [Enemy + reviving] (only when non-empty; fallen enemies still in the fight)}; {error} when the combat
// state is unavailable. Enemy: {entity_id ("<monster id>_<n>", n counted per id, living and dead counted apart),
// combat_id, name, hp, max_hp, block, status (Game.Powers.Of), intents?: [{type, damage?, repeats?, label, title?,
// description?}], move?: {id, follow_up_id, must_perform_once, performed_at_least_once, state_log?}, private_state?
// (Game.PrivateData.Of(monster, typeof(MonsterModel)))}.
internal static class BattleScreen
{
    private const int StateLogLength = 8;

    // The run and room are what the observer knows; the fight itself lives in the combat manager.
    public static Dictionary<string, object?> Read(RunState run, CombatRoom room)
    {
        var combat = Combat.State;
        if (combat == null) return new() { ["error"] = "Combat state unavailable" };

        // Living enemies and fallen ones are listed apart, each numbered on its own, so a client that treats "enemies" as
        // the targets keeps working when a Parafright, a Decimillipede segment or a Test Subject lies waiting to come back.
        var living = new List<Dictionary<string, object?>>();
        var fallen = new List<Dictionary<string, object?>>();
        var livingCounts = new Dictionary<string, int>();
        var fallenCounts = new Dictionary<string, int>();
        foreach (var creature in combat.Enemies)
        {
            if (creature.IsAlive)
            {
                living.Add(Enemy(creature, Combat.EntityId(creature, livingCounts)));
                continue;
            }
            try
            {
                var enemy = Enemy(creature, Combat.EntityId(creature, fallenCounts));
                enemy["reviving"] = WaitingToRevive(creature);
                fallen.Add(enemy);
            }
            catch { /* a fallen enemy that cannot be described is left out */ }
        }

        var battle = new Dictionary<string, object?>
        {
            ["round"] = combat.RoundNumber,
            ["turn"] = combat.CurrentSide.ToString().ToLower(),
            ["is_play_phase"] = Combat.IsPlayPhase(combat),
            ["enemies"] = living
        };
        if (fallen.Count > 0) battle["dead_enemies"] = fallen;
        return battle;
    }

    // One enemy under the entity_id Combat.EntityId gave it (numbered per list: the caller keeps a dictionary each for
    // the living and the fallen).
    public static Dictionary<string, object?> Enemy(Creature creature, string entityId)
    {
        var monster = creature.Monster;
        var enemy = new Dictionary<string, object?>
        {
            ["entity_id"] = entityId,
            ["combat_id"] = creature.CombatId,
            ["name"] = Text.Of(() => monster?.Title),
            ["hp"] = creature.CurrentHp,
            ["max_hp"] = creature.MaxHp,
            ["block"] = creature.Block,
            ["status"] = Powers.Of(creature)
        };
        if (monster == null) return enemy;

        if (monster.NextMove is { } move)
        {
            enemy["intents"] = Intents(move, creature);
            try { enemy["move"] = Move(monster, move); } catch { /* optional detail */ }
        }
        try { if (PrivateData.Of(monster, typeof(MonsterModel)) is { } data) enemy["private_state"] = data; } catch { /* optional detail */ }
        return enemy;
    }

    // Fell but will come back (Illusion/Adaptable "isReviving", or a RESPAWN/REVIVE/DEAD move state).
    public static bool WaitingToRevive(Creature creature)
    {
        try
        {
            // The raw flag decides when it can be read; a reviving power without a readable flag still counts.
            bool illusory = false;
            foreach (var power in creature.Powers)
            {
                if (power is not (IllusionPower or AdaptablePower)) continue;
                if (Powers.InternalField(power, "isReviving") is bool reviving) return reviving;
                illusory = true;
            }
            if (illusory) return true;

            var state = creature.Monster?.NextMove?.StateId ?? "";
            return state.Contains("RESPAWN", StringComparison.OrdinalIgnoreCase)
                || state.Contains("REVIVE", StringComparison.OrdinalIgnoreCase)
                || state.Contains("DEAD", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // {type, damage?, repeats?, label, title?, description?} per intent of the move. Damage, label and tip are resolved
    // against the player creatures the intent is aimed at; an intent whose text fails keeps the fields resolved before it.
    private static List<Dictionary<string, object?>> Intents(MoveState move, Creature owner)
    {
        var intents = new List<Dictionary<string, object?>>();
        foreach (var intent in move.Intents)
        {
            var entry = new Dictionary<string, object?> { ["type"] = intent.IntentType.ToString() };
            intents.Add(entry);
            try
            {
                if (owner.CombatState?.PlayerCreatures is not { } targets) continue;
                if (intent is AttackIntent attack)
                {
                    entry["damage"] = attack.GetSingleDamage(targets, owner);
                    entry["repeats"] = Math.Max(1, attack.Repeats);
                }
                entry["label"] = Text.Plain(intent.GetIntentLabel(targets, owner).GetFormattedText());
                var tip = intent.GetHoverTip(targets, owner);
                if (tip.Title != null) entry["title"] = Text.Plain(tip.Title);
                if (tip.Description != null) entry["description"] = Text.Plain(tip.Description);
            }
            catch { /* the label of some intent types fails outside its turn */ }
        }
        return intents;
    }

    // The move state itself and the tail of the state machine's log (branch states that hide from logs excluded).
    private static Dictionary<string, object?> Move(MonsterModel monster, MoveState move)
    {
        var entry = new Dictionary<string, object?>
        {
            ["id"] = move.StateId,
            ["follow_up_id"] = move.FollowUpStateId,
            ["must_perform_once"] = move.MustPerformOnceBeforeTransitioning,
            ["performed_at_least_once"] = Nodes.Field(move, "_performedAtLeastOnce") as bool?
        };
        if (monster.MoveStateMachine?.StateLog is { } log)
            entry["state_log"] = log.Where(s => s.ShouldAppearInLogs).Select(s => s.Id).TakeLast(StateLogLength).ToList();
        return entry;
    }
}
