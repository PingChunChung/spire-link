using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rooms;
using SpireLink.Game;

namespace SpireLink.State.Screens;

// Group C. The "treasure" field.
// Contract: {message: "Treasure room loading..."} without an NTreasureRoom; {can_open_chest, message} while the chest is
// closed; otherwise {can_open_chest, relics?: [Relics.Choice...] (enabled, visible holders of %RelicCollection when it is
// visible), can_proceed}. Observing never opens the chest.
// The chest button is the room's direct child "Chest"; it is disabled the moment it is clicked, so an enabled button
// means the chest is still closed. The relic collection shows while a relic is being picked; the holders are indexed in
// tree order, counting only those that can be clicked.
internal static class TreasureScreen
{
    public static Dictionary<string, object?> Read(TreasureRoom room)
    {
        var ui = Nodes.First<NTreasureRoom>(Nodes.Root);
        if (ui == null) return new() { ["message"] = "Treasure room loading..." };

        var chest = ui.GetNodeOrNull<NClickableControl>("Chest");
        var state = new Dictionary<string, object?> { ["can_open_chest"] = chest is { IsEnabled: true, Visible: true } };
        if (chest is { IsEnabled: true })
        {
            state["message"] = "Chest is closed. Use open_chest.";
            return state;
        }

        var collection = ui.GetNodeOrNull<NTreasureRoomRelicCollection>("%RelicCollection");
        if (collection?.Visible == true)
        {
            var relics = new List<Dictionary<string, object?>>();
            foreach (var holder in Nodes.All<NTreasureRoomRelicHolder>(collection))
                if (holder.IsEnabled && holder.Visible && holder.Relic?.Model is { } relic)
                    relics.Add(Relics.Choice(relic, relics.Count));
            state["relics"] = relics;
        }
        state["can_proceed"] = ui.ProceedButton?.IsEnabled ?? false;
        return state;
    }
}
