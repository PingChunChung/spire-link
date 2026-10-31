using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace SpireLink;

// Punch Off hang (seed HUNTSSEED950, fleet lane 03): PunchOff.PunchEachOther loops without ever yielding when the bridge's non_interactive
// switch (or Instant animations) turns every Cmd.Wait into a completed task. The journal named ENDLESS_CONVEYOR because the event
// synchronizer still holds the previous event until BeginEvent (see Events.Ready). See BridgeStall.cs for the pure logic.
public static partial class SpireLinkMod
{
    private const long StallThresholdMs = 10_000;
    private static StallWatch? _stallWatch;
    private static readonly EpisodeLatch PunchOffEpisode = new();
    private static int _punchOffSkipped;

    private static void InstallPunchOffGuard()
    {
        try
        {
            _harmony ??= new Harmony("spire-link.guards");
            var target = AccessTools.Method(typeof(PunchOff), "PunchEachOther");
            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(SpireLinkMod), nameof(PunchEachOtherPrefix)));
            _journal?.Record("punch_off_guard_installed", new { target = "PunchOff.PunchEachOther" });
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Spire Link] punch off guard not installed: {ex}");
            _journal?.Record("punch_off_guard_failed", new { error_type = ex.GetType().Name, message = ex.Message });
        }
    }

    // Replaces only the decorative loop; the options, the Injury curse, the relic and the fight are untouched (TakeThem and OnRoomExited use `_punchCts?`).
    private static bool PunchEachOtherPrefix(ref Task __result)
    {
        try
        {
            bool instant = SaveManager.Instance?.PrefsSave?.FastMode == FastModeType.Instant;
            bool active = MegaCrit.Sts2.Core.Helpers.NonInteractiveMode.IsActive;
            if (!PunchOffLoop.ShouldSkip(_configuration.NonInteractive, active, instant)) return true;
            __result = Task.CompletedTask;
            Interlocked.Increment(ref _punchOffSkipped);
            string? id = null;
            try { id = (RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom as EventRoom)?.CanonicalEvent.Id.Entry; } catch { }
            GD.PrintErr($"[Spire Link] PunchOff.PunchEachOther skipped (it would spin the main thread: non_interactive={_configuration.NonInteractive}, instant={instant}, event {id})");
            _journal?.Record("punch_off_loop_skipped", new { event_id = id ?? "PUNCH_OFF", non_interactive = _configuration.NonInteractive,
                instant_animations = instant, auto_slayer_active = active });
            return false;
        }
        catch (Exception ex)
        {
            Count("punch_off_guard_error");
            _lastError = ex.Message;
            return true;
        }
    }

    private static void StartMainThreadWatchdog()
    {
        _stallWatch = new StallWatch(StallThresholdMs, System.Environment.TickCount64);
        var thread = new Thread(WatchdogLoop) { IsBackground = true, Name = "spire-link-watchdog" };
        thread.Start();
    }

    private static void BeatMainThread() => _stallWatch?.Beat(System.Environment.TickCount64);

    private static void WatchdogLoop()
    {
        while (!Stopping.IsCancellationRequested)
        {
            try
            {
                Thread.Sleep(1000);
                var watch = _stallWatch;
                if (watch == null) continue;
                long now = System.Environment.TickCount64;
                switch (watch.Poll(now))
                {
                    case StallTransition.Stalled:
                    {
                        // Ids only, read without touching the scene tree (the main thread is the one that is stuck).
                        string? room = null, eventId = null;
                        try
                        {
                            var current = RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom;
                            room = current?.GetType().Name;
                            if (current is EventRoom er) eventId = er.CanonicalEvent.Id.Entry;
                        }
                        catch (Exception) { }
                        GD.PrintErr($"[Spire Link] main thread has not run a frame for {watch.StalledForMs(now)} ms (room {room}, event {eventId})");
                        _journal?.Record("main_thread_stalled", new { stalled_ms = watch.StalledForMs(now), room_type = room, event_id = eventId,
                            observed_event_id = _currentEventId, punch_off_loops_skipped = Volatile.Read(ref _punchOffSkipped), last_error = _lastError });
                        break;
                    }
                    case StallTransition.Resumed:
                        _journal?.Record("main_thread_resumed", new { });
                        break;
                }
            }
            catch (Exception ex) { _lastError = ex.Message; }
        }
    }
}
