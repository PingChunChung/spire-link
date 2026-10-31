namespace SpireLink.Game;

// The card reward screen's alternatives (skip, reroll, an event's own choices): which button belongs to which option id.
// Kept free of game types so the rule has an offline test (tests/CardRewardAlternativeTests).
public static class CardRewardAlternatives
{
    public const string SkipOptionId = "Skip";
    public const string RerollOptionId = "REROLL";

    // RefreshOptions creates one button per alternative and binds that alternative's
    // index to its Released signal. Resolve the ID first; never assume Skip is first.
    public static int IndexOf(
        IReadOnlyList<string> optionIds, int liveButtonCount, string optionId)
    {
        if (optionIds.Count != liveButtonCount)
            return -1;

        int match = -1;
        for (int i = 0; i < optionIds.Count; i++)
        {
            if (!string.Equals(optionIds[i], optionId, StringComparison.Ordinal))
                continue;
            if (match >= 0)
                return -1;
            match = i;
        }
        return match;
    }
}
