using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline.UnlockScreens;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline;
using SpireLink.Actions;
using SpireLink.Game;
using SpireLink.State.Screens;

namespace SpireLink.Menus;

// The timeline: "advance"/"proceed" clicks whatever the screen is waiting on (first-time tutorial, confirm or proceed
// button, an open epoch inspect, a queued unlock screen) and reports done when nothing is left; "back" leaves the screen.
// Unlock screens are only opened when the game itself queued them: an obtained epoch is never revealed by automation,
// because entering that state through code makes the game log invalid unlock-state errors.
internal static class TimelineMenu
{
    public static Dictionary<string, object?> Select(NTimelineScreen timeline, Node root, string option)
    {
        if (MenuActions.Is(option, "advance") || MenuActions.Is(option, "proceed")) return Advance(timeline, root);
        if (MenuActions.Is(option, "back")) return Back(timeline);
        return Outcome.Fail($"Unknown timeline option: {option}. Use: advance, back");
    }

    private static Dictionary<string, object?> Advance(NTimelineScreen timeline, Node root)
    {
        if (Nodes.Shown<NTimelineTutorial>(root) is { } tutorial && Nodes.Field(tutorial, "_acknowledgeButton") is NClickableControl acknowledge && Ui.Enabled(acknowledge))
            return Click(acknowledge, "Clicked tutorial proceed button");
        if (Nodes.First<NConfirmButton>(root) is { } confirm && Ui.Enabled(confirm)) return Click(confirm, "Clicked confirm button");
        if (Nodes.First<NProceedButton>(root) is { } proceed && Ui.Enabled(proceed)) return Click(proceed, "Clicked proceed button");

        if (Nodes.Shown<NEpochInspectScreen>(root) is { } inspect)
        {
            if (Nodes.Field(inspect, "_closeButton") is NClickableControl close && Ui.Enabled(close)) close.ForceClick();
            else inspect.Close();
            return Outcome.Ok("Closed epoch inspect screen");
        }

        if (QueuedUnlock(timeline) is { } queued) return queued;

        var pending = MenuScreens.PendingEpochIds();
        if (pending.Count > 0)
            return Outcome.Ok("Epoch unlocks are obtained but not revealed; not forcing timeline reveal from automation",
                ("pending_epoch_ids", pending), ("manual_action_required", true), ("done", true));
        return Outcome.Ok("No more epochs to advance", ("done", true));
    }

    private static Dictionary<string, object?> Back(NTimelineScreen timeline)
    {
        if (Nodes.Field(timeline, "_backButton") is NClickableControl back && back.IsEnabled) return Click(back, "Going back from timeline");
        if (Nodes.Field(timeline, "_stack") is NSubmenuStack stack)
        {
            stack.Pop();
            return Outcome.Ok("Popped timeline from stack");
        }
        return Outcome.Fail("Back button not available on timeline");
    }

    // The unlock screen the game queued, opened when it is safe to; null when none is queued. Every reply here is "ok":
    // a retry flag asks the caller to poll again, done means the queue should be left to the player.
    private static Dictionary<string, object?>? QueuedUnlock(NTimelineScreen timeline)
    {
        bool isQueued;
        try { isQueued = timeline.IsScreenQueued(); }
        catch (ObjectDisposedException)
        {
            return Outcome.Ok("Timeline changed while checking queued unlocks; retry after the next state poll", ("retry", true));
        }
        if (!isQueued) return null;

        var screen = Peek(timeline);
        var type = screen?.GetType().Name ?? "unlock";
        var ids = EpochIds(screen);
        var revealed = ids.Where(id => StateOf(id) == EpochState.Revealed).ToList();
        if (revealed.Count > 0)
        {
            var rest = ids.Except(revealed, StringComparer.OrdinalIgnoreCase).ToList();
            return Outcome.Ok("Queued timeline unlock references already revealed epochs; not opening it to avoid an invalid unlock path",
                ("queued_unlock_type", type), ("already_revealed_epoch_ids", revealed), ("pending_epoch_ids", rest),
                ("manual_action_required", rest.Count > 0), ("done", true));
        }
        if (screen is NUnlockTimelineScreen && Busy(timeline))
            return Outcome.Ok("Timeline expansion is queued, but the timeline is still animating; retry after the next state poll",
                ("queued_unlock_type", type), ("pending_epoch_ids", ids), ("retry", true));

        try
        {
            timeline.OpenQueuedScreen();
            return Outcome.Ok($"Opening queued {type}", ("queued_unlock_type", type), ("pending_epoch_ids", ids));
        }
        catch (ObjectDisposedException)
        {
            return Outcome.Ok("Timeline changed before the queued unlock could open; retry after the next state poll", ("queued_unlock_type", type), ("retry", true));
        }
        catch (Exception ex)
        {
            return Outcome.Ok($"Skipped queued {type}: {ex.Message}", ("queued_unlock_type", type), ("retry", true));
        }
    }

    private static NUnlockScreen? Peek(NTimelineScreen timeline)
    {
        try { return Nodes.Field(timeline, "_unlockScreens") is Queue<NUnlockScreen> { Count: > 0 } queue ? queue.Peek() : null; }
        catch { return null; }
    }

    // The epochs an unlock screen is about: one for a character unlock, a list for an epoch reveal, era slots for an expansion.
    private static List<string> EpochIds(NUnlockScreen? screen)
    {
        IEnumerable<EpochModel?> models = screen switch
        {
            NUnlockCharacterScreen => new[] { Nodes.Field(screen, "_epoch") as EpochModel },
            NUnlockEpochScreen => Nodes.Field(screen, "_unlockedEpochs") as IEnumerable<EpochModel?> ?? Enumerable.Empty<EpochModel?>(),
            NUnlockTimelineScreen => (Nodes.Field(screen, "_erasToUnlock") as IEnumerable<EpochSlotData> ?? Enumerable.Empty<EpochSlotData>()).Select(slot => slot?.Model),
            _ => Enumerable.Empty<EpochModel?>()
        };
        return models.Select(model => model?.Id).OfType<string>().Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // While the timeline hides its UI or blocks input it is still animating.
    private static bool Busy(NTimelineScreen timeline)
    {
        try
        {
            if (Nodes.Field(timeline, "_isUiVisible") is false) return true;
            return Nodes.Field(timeline, "_inputBlocker") is Control blocker && Nodes.Visible(blocker);
        }
        catch (ObjectDisposedException) { return true; }
    }

    private static EpochState? StateOf(string epochId)
    {
        try
        {
            return SaveManager.Instance?.Progress?.Epochs
                .FirstOrDefault(epoch => string.Equals(epoch.Id, epochId, StringComparison.OrdinalIgnoreCase))?.State;
        }
        catch { return null; }
    }

    private static Dictionary<string, object?> Click(NClickableControl button, string done)
    {
        button.ForceClick();
        return Outcome.Ok(done);
    }
}
