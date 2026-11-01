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

        // non_interactive: every mode shares the exclusions (merchant room, transform selection and its cooldown).
        Check(NonInteractivePolicy.Normalize(null) == "off" && NonInteractivePolicy.Normalize(" All ") == "all" && NonInteractivePolicy.Normalize("NoShop") == "noshop",
            "supported modes normalize");
        Check(NonInteractivePolicy.Normalize("fast") == null && NonInteractivePolicy.Normalize("") == "off", "an unknown mode is reported, an empty one is off");
        const long never = long.MinValue;
        Check(!NonInteractivePolicy.Allow("off", true, false, false, 100_000, never), "off never allows the fast path");
        foreach (var mode in new[] { "combat", "noshop", "all" })
        {
            Check(!NonInteractivePolicy.Allow(mode, true, true, false, 100_000, never), mode + ": the merchant room runs normally");
            Check(!NonInteractivePolicy.Allow(mode, true, false, true, 100_000, 100_000), mode + ": the transform selection runs normally");
            Check(!NonInteractivePolicy.Allow(mode, true, false, false, 105_000, 100_000), mode + ": the transform animation after the screen runs normally");
            Check(NonInteractivePolicy.Allow(mode, true, false, false, 100_000 + NonInteractivePolicy.TransformCooldownMs, 100_000), mode + ": the fast path returns after the cooldown");
            Check(NonInteractivePolicy.Allow(mode, true, false, false, 100_000, never), mode + ": combat runs on the fast path");
        }
        Check(!NonInteractivePolicy.Allow("combat", false, false, false, 100_000, never), "combat: outside a fight the game runs normally");
        Check(NonInteractivePolicy.Allow("noshop", false, false, false, 100_000, never) && NonInteractivePolicy.Allow("all", false, false, false, 100_000, never),
            "noshop and all: outside a fight the fast path stays on");

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
