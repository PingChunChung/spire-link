using SpireLink;

static class Tests
{
    private static int _count;
    private static void Check(bool condition, string message)
    {
        _count++;
        if (!condition) throw new Exception(message);
    }

    public static void Main()
    {
        // Punch Off loop is skipped whenever a wait could be skipped; left alone only in a plain interactive game.
        Check(PunchOffLoop.ShouldSkip("all", false, false), "non_interactive=all skips");
        Check(PunchOffLoop.ShouldSkip("combat", false, false), "non_interactive=combat skips");
        Check(PunchOffLoop.ShouldSkip("noshop", false, false), "non_interactive=noshop skips");
        Check(PunchOffLoop.ShouldSkip("off", true, false), "auto slayer active skips");
        Check(PunchOffLoop.ShouldSkip("off", false, true), "instant animations skip");
        Check(PunchOffLoop.ShouldSkip(null, false, true), "null config + instant skips");
        Check(!PunchOffLoop.ShouldSkip("off", false, false), "plain game keeps the animation");
        Check(!PunchOffLoop.ShouldSkip(null, false, false), "null config means off");
        Check(!PunchOffLoop.ShouldSkip("OFF", false, false), "case-insensitive off");

        // Watchdog: one Stalled per episode, one Resumed.
        var w = new StallWatch(10_000, 0);
        Check(w.Poll(5_000) == StallTransition.None, "under threshold");
        w.Beat(6_000);
        Check(w.Poll(15_000) == StallTransition.None, "beat resets idle time");
        Check(w.Poll(16_000) == StallTransition.Stalled, "stall after threshold");
        Check(w.StalledForMs(16_000) == 10_000, "stalled ms");
        Check(w.Poll(17_000) == StallTransition.None && w.Poll(60_000) == StallTransition.None, "stall reported once");
        w.Beat(61_000);
        Check(w.Poll(61_500) == StallTransition.Resumed, "resume reported");
        Check(w.Poll(62_000) == StallTransition.None, "resume reported once");
        Check(w.Poll(80_000) == StallTransition.Stalled, "a new episode reports again");

        Console.WriteLine($"StallTests passed: {_count} checks");
    }
}
