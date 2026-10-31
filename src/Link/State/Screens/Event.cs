using Godot;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using SpireLink.Game;

namespace SpireLink.State.Screens;

// Group B. The "event" field.
// Contract (Read): {event_id, event_name, is_ancient, in_dialogue, body, options: [{index, title, description, is_locked,
// actionable, is_proceed, was_chosen, relic_name?, relic_description?, keywords}]}. Options come from the NEventOptionButtons
// of NEventRoom.Instance; a combat-layout event (no NEventRoom) lists the model's CurrentOptions with source: "model".
// Contract (Minimal, when Read throws): {fallback_reason, event_id, is_ancient: false, in_dialogue: false, options: [{index,
// title, description, is_locked, is_proceed, was_chosen, actionable}]} read straight from the buttons.
internal static class EventScreen
{
    public static Dictionary<string, object?> Read(EventRoom room)
    {
        var model = Events.Current(room);
        bool ancient = model is AncientEventModel;
        var ui = NEventRoom.Instance;
        bool inDialogue = ancient && DialogueHitbox(Nodes.First<NAncientEventLayout>(ui)) != null;
        return new()
        {
            ["event_id"] = model.Id.Entry,
            ["event_name"] = Text.Of(() => model.Title),
            ["is_ancient"] = ancient,
            ["in_dialogue"] = inDialogue,
            ["body"] = Text.Of(() => model.Description),
            ["options"] = ui != null ? ButtonOptions(ui, inDialogue) : ModelOptions(model)
        };
    }

    // The clickable dialogue hitbox while the ancient is still talking (the option buttons wait behind it), else null.
    // in_dialogue and the advance_dialogue action share it.
    public static NClickableControl? DialogueHitbox(NAncientEventLayout? layout)
    {
        if (!Nodes.Visible(layout)) return null;
        var hitbox = layout!.GetNodeOrNull<NClickableControl>("%DialogueHitbox");
        return Ui.Enabled(hitbox) ? hitbox : null;
    }

    private static List<Dictionary<string, object?>> ButtonOptions(NEventRoom ui, bool inDialogue)
    {
        var options = new List<Dictionary<string, object?>>();
        foreach (var button in Nodes.All<NEventOptionButton>(ui))
        {
            var option = button.Option;
            var entry = Option(option, options.Count, !inDialogue && !option.IsLocked && Ui.Enabled(button));
            if (option.Relic is { } relic)
            {
                entry["relic_name"] = Text.Of(() => relic.Title);
                entry["relic_description"] = Text.Of(() => relic.DynamicDescription);
            }
            entry["keywords"] = Tips.Of(option.HoverTips);
            options.Add(entry);
        }
        return options;
    }

    // Events with a combat layout (Punch Off, ...) have no event room or buttons: the model's own options are answered
    // through the event synchronizer, so every unlocked one is actionable.
    private static List<Dictionary<string, object?>> ModelOptions(EventModel model)
    {
        var options = new List<Dictionary<string, object?>>();
        foreach (var option in model.CurrentOptions)
        {
            var entry = Option(option, options.Count, !option.IsLocked);
            entry["source"] = "model";
            entry["keywords"] = Tips.Of(option.HoverTips);
            options.Add(entry);
        }
        return options;
    }

    private static Dictionary<string, object?> Option(EventOption option, int index, bool actionable) => new()
    {
        ["index"] = index,
        ["title"] = Text.Of(() => option.Title),
        ["description"] = Text.Of(() => option.Description),
        ["is_locked"] = option.IsLocked,
        ["actionable"] = actionable,
        ["is_proceed"] = option.IsProceed,
        ["was_chosen"] = option.WasChosen
    };

    // Last resort when the event cannot be described in full: whatever each button yields, field by field, so the player
    // still has something to choose.
    public static Dictionary<string, object?> Minimal(EventRoom room, Exception cause)
    {
        var state = new Dictionary<string, object?> { ["fallback_reason"] = cause.GetType().Name + ": " + cause.Message };
        state["event_id"] = Try(() => Events.Current(room).Id.Entry, null);
        state["is_ancient"] = false;
        state["in_dialogue"] = false;
        var options = new List<Dictionary<string, object?>>();
        try
        {
            foreach (var button in Nodes.All<NEventOptionButton>(NEventRoom.Instance))
            {
                var option = button.Option;
                options.Add(new()
                {
                    ["index"] = options.Count,
                    ["title"] = Try(() => Text.Of(() => option.Title), null),
                    ["description"] = Try(() => Text.Of(() => option.Description), null),
                    ["is_locked"] = Try(() => option.IsLocked, false),
                    ["is_proceed"] = Try(() => option.IsProceed, false),
                    ["was_chosen"] = Try(() => option.WasChosen, false),
                    ["actionable"] = Try(() => !option.IsLocked && Ui.Enabled(button), true)
                });
            }
        }
        catch { /* the options read so far */ }
        state["options"] = options;
        return state;
    }

    private static T Try<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    public static Dictionary<string, object?>? ReadOrMinimal(EventRoom room)
    {
        try { return Read(room); }
        catch (Exception ex)
        {
            GD.PrintErr($"[Spire Link] event state fallback: {ex.GetType().Name}: {ex.Message}");
            return Minimal(room, ex);
        }
    }
}
