using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SpireLink.Game;
using SpireLink.State.Screens;

namespace SpireLink.State;

// Which screen the player is looking at, and its state as the /v1/observe body (before the bridge adds its own context).
// The order matters: a tutorial or popup covers everything; outside a run only menus exist; in a run the overlay stack
// is read first, then the map, then the room. Rewards, card reward and Crystal Sphere overlays linger on the stack after
// Proceed while the map opens on top, so the map wins over those three.
// A screen reader returns null when the UI it describes is not wired yet (the hand before its selection prompt, a
// character select without a lobby); the whole observation is then null and the bridge reports an unsupported state.
internal static class Observer
{
    public static Dictionary<string, object?>? Read()
    {
        var root = Nodes.Root;
        if (!FtueScreens.TryRead(root, out var covering)) return null;
        if (covering != null) return covering;

        var manager = RunManager.Instance;
        if (!manager.IsInProgress) return MenuScreens.Read(root);

        var run = manager.DebugOnlyGetState();
        if (run == null)
            return Nodes.Shown<NCharacterSelectScreen>(root) is { } select ? CharacterSelectScreen.Read(select) : Unknown(null);

        var state = new Dictionary<string, object?>();
        if (!ReadScreen(state, run, root)) return null;
        state["run"] = Players.Run(run);
        if (LocalContext.GetMe(run) is { } me) state["player"] = Players.State(me);
        return state;
    }

    // Fills state_type and the screen's own field; false when that screen's reader could not describe it yet.
    private static bool ReadScreen(Dictionary<string, object?> state, RunState run, Node? root)
    {
        var top = Ui.TopOverlay;
        var room = run.CurrentRoom;
        bool mapOpen = Ui.MapOpen;
        switch (top)
        {
            case NCardGridSelectionScreen grid: return Put(state, "card_select", CardSelectScreen.ReadGrid(grid));
            case NChooseACardSelectionScreen choose: return Put(state, "card_select", CardSelectScreen.ReadChoice(choose));
            case NChooseABundleSelectionScreen bundle: return Put(state, "bundle_select", BundleSelectScreen.Read(bundle));
            case NChooseARelicSelection relic: return Put(state, "relic_select", RelicSelectScreen.Read(relic));
            case NCrystalSphereScreen sphere when !mapOpen: return Put(state, "crystal_sphere", CrystalSphereScreen.Read(sphere));
            case NCardRewardSelectionScreen reward when !mapOpen: return Put(state, "card_reward", CardRewardScreen.Read(reward));
            case NRewardsScreen rewards when !mapOpen: return Put(state, "rewards", RewardsScreen.Read(rewards));
            case NGameOverScreen over: return Put(state, "game_over", GameOverScreen.Read(over));
            case IOverlayScreen other and not (NRewardsScreen or NCardRewardSelectionScreen or NCrystalSphereScreen):
                return Put(state, "overlay", OverlayScreen.Read(other));
        }
        if (mapOpen || room is MapRoom) return Put(state, "map", MapScreen.Read(run));

        switch (room)
        {
            case CombatRoom combat:
            {
                string type = combat.RoomType.ToString().ToLower();
                if (!Combat.InProgress)
                {
                    state["state_type"] = type;
                    state["message"] = "Combat ended. Waiting for rewards...";
                    return true;
                }
                var hand = NPlayerHand.Instance;
                if (hand is { IsInCardSelection: true })
                {
                    if (HandSelectScreen.Read(hand) is not { } selection) return false;
                    state["state_type"] = "hand_select";
                    state["hand_select"] = selection;
                }
                else state["state_type"] = type;
                if (BattleScreen.Read(run, combat) is not { } battle) return false;
                state["battle"] = battle;
                return true;
            }
            case EventRoom eventRoom:
                if (!Events.Ready(eventRoom))
                {
                    state["state_type"] = "transition";
                    state["reason"] = "event_not_ready";
                    return true;
                }
                if (Events.Current(eventRoom) is MegaCrit.Sts2.Core.Models.Events.FakeMerchant)
                    return Put(state, "fake_merchant", FakeMerchantScreen.ReadOrError(eventRoom));
                return Put(state, "event", EventScreen.ReadOrMinimal(eventRoom));
            case MerchantRoom merchant: return Put(state, "shop", ShopScreen.Read(merchant));
            case RestSiteRoom rest: return Put(state, "rest_site", RestSiteScreen.Read(rest));
            case TreasureRoom treasure: return Put(state, "treasure", TreasureScreen.Read(treasure));
            case null when Nodes.Shown<NCharacterSelectScreen>(root) is { } select:
            {
                var menu = CharacterSelectScreen.Read(select);
                if (menu == null) return false;
                foreach (var (key, value) in menu) state[key] = value;
                return true;
            }
            default:
                foreach (var (key, value) in Unknown(room)) state[key] = value;
                return true;
        }
    }

    private static bool Put(Dictionary<string, object?> state, string type, Dictionary<string, object?>? body)
    {
        if (body == null) return false;
        state["state_type"] = type;
        state[type] = body;
        return true;
    }

    private static Dictionary<string, object?> Unknown(AbstractRoom? room)
        => new() { ["state_type"] = "unknown", ["room_type"] = room?.GetType().Name };
}
