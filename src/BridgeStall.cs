using System;
using System.Threading;

namespace SpireLink;

// Pure logic of the Punch Off loop guard and the main-thread watchdog (no game types, so tests/StallTests.cs can compile it).
public static class PunchOffLoop
{
    // PunchOff.PunchEachOther is `while (!cancelled) { await TriggerAnim; await Cmd.Wait(...); ... }`. Cmd.Wait and CustomScaledWait return
    // a completed task without yielding when NonInteractiveMode.IsActive (the bridge's non_interactive switch) or FastMode is Instant,
    // so the loop never gives the frame back and spins on the main thread forever. It is only decoration (enemies punching each other),
    // so it is skipped whenever any wait could be skipped.
    public static bool ShouldSkip(string? nonInteractiveConfig, bool autoSlayerCheckActive, bool instantAnimations) =>
        autoSlayerCheckActive || instantAnimations || !string.Equals(nonInteractiveConfig ?? "off", "off", StringComparison.OrdinalIgnoreCase);
}

public enum StallTransition { None, Stalled, Resumed }

// Heartbeat from the main thread (once per frame), polled from another thread. Reports a stall once per episode and the resume.
public sealed class StallWatch
{
    private readonly long _thresholdMs;
    private long _lastBeatMs;
    private bool _stalled;
    private long _stallStartedMs;

    public StallWatch(long thresholdMs, long nowMs) { _thresholdMs = thresholdMs; _lastBeatMs = nowMs; }

    public long LastBeatMs => Interlocked.Read(ref _lastBeatMs);
    public long StalledForMs(long nowMs) => Math.Max(0, nowMs - LastBeatMs);
    public void Beat(long nowMs) => Interlocked.Exchange(ref _lastBeatMs, nowMs);

    // Called by the watchdog thread only.
    public StallTransition Poll(long nowMs)
    {
        long idle = nowMs - LastBeatMs;
        if (!_stalled)
        {
            if (idle < _thresholdMs) return StallTransition.None;
            _stalled = true;
            _stallStartedMs = LastBeatMs;
            return StallTransition.Stalled;
        }
        if (LastBeatMs > _stallStartedMs) { _stalled = false; return StallTransition.Resumed; }
        return StallTransition.None;
    }
}
