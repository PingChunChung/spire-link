using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SpireLink.Game;

namespace SpireLink;

// Guards NCardGrid.UpdateGridNavigation (see BridgeCardGrid.cs for the cause) and records the event room being entered.
public static partial class SpireLinkMod
{
    private static readonly EpisodeLatch CardGridEpisode = new();
    private static string? _currentEventId;
    private static string? _lastEventRoomKey;
    private static Harmony? _harmony;

    // The mod has a [ModInitializer], so the game does not call PatchAll for it: patches are applied by hand.
    private static void InstallCardGridGuard()
    {
        try
        {
            _harmony = new Harmony("spire-link.guards");
            var target = AccessTools.Method(typeof(NCardGrid), "UpdateGridNavigation");
            var prefix = new HarmonyMethod(typeof(SpireLinkMod), nameof(CardGridNavigationPrefix));
            var finalizer = new HarmonyMethod(typeof(SpireLinkMod), nameof(CardGridNavigationFinalizer));
            _harmony.Patch(target, prefix: prefix, finalizer: finalizer);
            _journal?.Record("card_grid_guard_installed", new { target = "NCardGrid.UpdateGridNavigation" });
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Spire Link] card grid guard not installed: {ex}");
            _journal?.Record("card_grid_guard_failed", new { error_type = ex.GetType().Name, message = ex.Message });
        }
    }

    // Runs the game's loop untouched when the rows are consistent. Otherwise sets the same focus neighbours with the Top index clamped.
    private static bool CardGridNavigationPrefix(NCardGrid __instance)
    {
        List<List<NGridCardHolder>>? rows = null;
        try
        {
            rows = AccessTools.Field(typeof(NCardGrid), "_cardRows")?.GetValue(__instance) as List<List<NGridCardHolder>>;
            if (rows == null) return true;
            var counts = rows.Select(r => r.Count).ToArray();
            bool inconsistent = !CardGridNavigation.IsConsistent(counts);
            if (CardGridEpisode.Enter(inconsistent))
            {
                int cards = (AccessTools.Field(typeof(NCardGrid), "_cards")?.GetValue(__instance) as System.Collections.ICollection)?.Count ?? -1;
                GD.PrintErr($"[Spire Link] NCardGrid rows inconsistent (row sizes {string.Join(",", counts)}, cards {cards}, event {_currentEventId}); using bounded focus navigation");
                _journal?.Record("card_grid_inconsistent", new { event_id = _currentEventId, row_counts = counts, card_count = cards,
                    grid_type = __instance.GetType().Name });
            }
            if (!inconsistent) return true;
            for (int i = 0; i < rows.Count; i++)
                for (int j = 0; j < rows[i].Count; j++)
                {
                    var n = CardGridNavigation.Of(counts, i, j);
                    var holder = rows[i][j];
                    holder.FocusNeighborLeft = rows[n.Left.Row][n.Left.Column].GetPath();
                    holder.FocusNeighborRight = rows[n.Right.Row][n.Right.Column].GetPath();
                    holder.FocusNeighborTop = rows[n.Top.Row][n.Top.Column].GetPath();
                    holder.FocusNeighborBottom = rows[n.Bottom.Row][n.Bottom.Column].GetPath();
                }
            return false;
        }
        catch (Exception ex)
        {
            // Never let the guard itself throw into the frame; the finalizer below still covers the original.
            Count("card_grid_guard_error");
            _lastError = ex.Message;
            return rows == null;
        }
    }

    // Backstop: an out-of-range inside the original loop (a shape the prefix did not foresee) must not escape into _Process.
    private static Exception? CardGridNavigationFinalizer(Exception? __exception)
    {
        if (__exception is not ArgumentOutOfRangeException) return __exception;
        Count("card_grid_swallowed");
        if (CardGridEpisode.Enter(true))
        {
            GD.PrintErr($"[Spire Link] NCardGrid.UpdateGridNavigation threw {__exception.GetType().Name} (event {_currentEventId}); swallowed");
            _journal?.Record("card_grid_swallowed", new { event_id = _currentEventId, message = __exception.Message });
        }
        return null;
    }

    // One event_room_entered record per event room, taken from the game's room (the observation has no event id while a card grid is on top).
    private static void ObserveEventRoom(AgentSnapshot snapshot, string run, string floor)
    {
        try
        {
            if (RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom is not EventRoom room)
            {
                _currentEventId = null;
                return;
            }
            if (!Events.Ready(room)) return;
            string eventId = Events.Current(room).Id.Entry;
            _currentEventId = eventId;
            string key = $"{run}:{floor}:{eventId}";
            if (key == _lastEventRoomKey) return;
            _lastEventRoomKey = key;
            _journal?.Record("event_room_entered", new { event_id = eventId, state_version = snapshot.Version, state_type = snapshot.StateType,
                run_id = run, floor });
        }
        catch (Exception ex) { Count("event_room_observe_error"); _lastError = ex.Message; }
    }
}
