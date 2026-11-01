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

// Which parts of the game run on the fast path (the game's own NonInteractiveMode: waits, pauses and per-frame pacing skipped)
// for each `non_interactive` setting. Every mode other than "off" shares the same safety exclusions: the fast path hangs the
// main thread on the transform card selection and on the transform animation that follows its confirm, and an early run saw
// the game crash when a merchant room loaded on the fast path (later doubted, the exclusion is kept as a precaution); those
// places always run at normal speed.
public static class NonInteractivePolicy
{
    public static readonly string[] SupportedModes = { "off", "combat", "noshop", "all" };
    // How long after the transform screen was last on top the normal path is kept, so its animation finishes.
    public const long TransformCooldownMs = 6000;

    // Returns the mode to use, or null when the configured value is not supported (the caller falls back to "off").
    public static string? Normalize(string? configured)
    {
        var mode = (configured ?? "").Trim().ToLowerInvariant();
        if (mode.Length == 0) return "off";
        return Array.IndexOf(SupportedModes, mode) >= 0 ? mode : null;
    }

    // True when the fast path may be active right now. `transformSeenAtMs` is the last time the transform screen was on top
    // (long.MinValue when never); the caller records it from `transformScreenOnTop`.
    public static bool Allow(string mode, bool combatInProgress, bool merchantRoom, bool transformScreenOnTop, long nowMs, long transformSeenAtMs)
    {
        if (mode == "off") return false;
        if (merchantRoom || transformScreenOnTop) return false;
        if (transformSeenAtMs != long.MinValue && nowMs - transformSeenAtMs < TransformCooldownMs) return false;
        if (mode == "combat") return combatInProgress;
        return true; // "noshop" and "all": everywhere except the exclusions above
    }
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
