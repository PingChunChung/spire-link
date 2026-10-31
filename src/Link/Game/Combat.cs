using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace SpireLink.Game;

// The turn phase. The game keeps it per player (PlayerCombatState.Phase); "play phase" means a fight is running,
// it is the player side's turn and the player is in Play.
internal static class Combat
{
    public static bool InProgress => CombatManager.Instance is { IsInProgress: true };

    public static CombatState? State => CombatManager.Instance?.DebugOnlyGetState();

    public static bool PlayerSideTurn => InProgress && State?.CurrentSide == CombatSide.Player;

    public static bool IsPlayPhase(Player? player)
        => PlayerSideTurn && player?.PlayerCombatState?.Phase == PlayerTurnPhase.Play;

    // Any player still in Play: the local player in singleplayer, at least one of them in co-op.
    public static bool IsPlayPhase(ICombatState? combat)
        => PlayerSideTurn && combat != null && combat.Players.Any(p => p?.PlayerCombatState?.Phase == PlayerTurnPhase.Play);

    // The entity_id the battle state reports and a target argument names: "<monster id>_<n>", n counting the creatures
    // with that id seen so far in `counts` ("jaw_worm_0", "jaw_worm_1"). The caller walks the fight's enemies in order
    // and keeps one dictionary per list it numbers (the living, the fallen).
    public static string EntityId(Creature creature, Dictionary<string, int> counts)
    {
        string id = creature.Monster?.Id.Entry ?? "unknown";
        counts.TryGetValue(id, out int seen);
        counts[id] = seen + 1;
        return $"{id}_{seen}";
    }
}
