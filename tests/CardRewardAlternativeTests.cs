using SpireLink.Game;

static class CardRewardAlternativeTests
{
    static int checks;

    static void Expect(int expected, string[] optionIds, int buttonCount, string optionId, string reason)
    {
        int actual = CardRewardAlternatives.IndexOf(optionIds, buttonCount, optionId);
        if (actual != expected)
            throw new Exception($"{reason}: expected {expected}, got {actual}");
        checks++;
    }

    static void Main()
    {
        var skip = CardRewardAlternatives.SkipOptionId;
        var reroll = CardRewardAlternatives.RerollOptionId;
        Expect(0, [skip, reroll], 2, skip, "Skip resolves to its own button");
        Expect(1, [skip, reroll], 2, reroll, "REROLL resolves to its own button");
        Expect(1, [reroll, skip], 2, skip, "Skip is independent of option order");
        Expect(0, [reroll, skip], 2, reroll, "REROLL is independent of option order");
        Expect(-1, [reroll], 1, skip, "REROLL alone cannot act as Skip");
        Expect(-1, [skip], 1, reroll, "Skip alone cannot act as REROLL");
        Expect(0, [reroll], 1, reroll, "non-skippable reward can still reroll");
        Expect(0, [skip], 1, skip, "Skip remains after reroll");
        Expect(-1, ["Heal", "Sacrifice"], 2, skip, "other alternatives cannot act as Skip");
        Expect(-1, ["Heal", "Sacrifice"], 2, reroll, "other alternatives cannot act as REROLL");
        Expect(-1, ["SKIP", "reroll"], 2, skip, "IDs use ordinal case-sensitive comparison");
        Expect(-1, ["跳過", "重抽"], 2, reroll, "localized titles cannot act as option IDs");
        Expect(-1, [], 0, skip, "no alternatives means no action");
        Expect(-1, [skip, reroll], 1, skip, "missing button rejects mapping");
        Expect(-1, [skip], 2, skip, "extra or stale button rejects mapping");
        Expect(-1, [skip, skip], 2, skip, "duplicate ID rejects ambiguous mapping");
        Expect(-1, [reroll, reroll], 2, reroll, "duplicate reroll ID rejects ambiguous mapping");
        Console.WriteLine($"Card reward alternative tests passed: {checks} assertions");
    }
}
