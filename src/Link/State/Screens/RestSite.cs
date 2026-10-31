using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using SpireLink.Game;

namespace SpireLink.State.Screens;

// Group C. The "rest_site" field.
// Contract: {options: [{index, id (OptionId), name, description, is_enabled}], can_proceed}.
// Options come from the room (Heal, Smith, relic-added ones), in the order the buttons are laid out; is_enabled is the
// option's own flag, so a disabled option can still be inspected. Proceed is the room's button, enabled once an option
// was taken or none is left.
internal static class RestSiteScreen
{
    public static Dictionary<string, object?> Read(RestSiteRoom room) => new()
    {
        ["options"] = room.Options.Select((option, index) => new Dictionary<string, object?>
        {
            ["index"] = index,
            ["id"] = option.OptionId,
            ["name"] = Text.Of(() => option.Title),
            ["description"] = Text.Of(() => option.Description),
            ["is_enabled"] = option.IsEnabled
        }).ToList(),
        ["can_proceed"] = NRestSiteRoom.Instance?.ProceedButton?.IsEnabled ?? false
    };
}
