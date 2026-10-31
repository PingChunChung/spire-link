using System.Text.Json;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Runs;

namespace SpireLink.Actions;

// Routes /v1/actions to the handler for each action name. Handlers live in one file per screen group.
internal static class Dispatcher
{
    private static readonly Dictionary<string, Func<ActionContext, Dictionary<string, object?>>> Handlers = new(StringComparer.Ordinal)
    {
        ["play_card"] = CombatActions.PlayCard,
        ["use_potion"] = CombatActions.UsePotion,
        ["discard_potion"] = CombatActions.DiscardPotion,
        ["end_turn"] = CombatActions.EndTurn,
        ["combat_select_card"] = CombatActions.SelectHandCard,
        ["combat_confirm_selection"] = CombatActions.ConfirmHandSelection,
        ["choose_map_node"] = RoomActions.ChooseMapNode,
        ["choose_event_option"] = RoomActions.ChooseEventOption,
        ["advance_dialogue"] = RoomActions.AdvanceDialogue,
        ["choose_rest_option"] = RoomActions.ChooseRestOption,
        ["shop_purchase"] = RoomActions.ShopPurchase,
        ["claim_reward"] = RoomActions.ClaimReward,
        ["select_card_reward"] = RoomActions.SelectCardReward,
        ["skip_card_reward"] = RoomActions.SkipCardReward,
        ["reroll_card_reward"] = RoomActions.RerollCardReward,
        ["proceed"] = RoomActions.Proceed,
        ["claim_treasure_relic"] = RoomActions.ClaimTreasureRelic,
        ["crystal_sphere_set_tool"] = RoomActions.CrystalSphereSetTool,
        ["crystal_sphere_click_cell"] = RoomActions.CrystalSphereClickCell,
        ["crystal_sphere_proceed"] = RoomActions.CrystalSphereProceed,
        ["select_card"] = SelectionActions.SelectCard,
        ["confirm_selection"] = SelectionActions.ConfirmSelection,
        ["cancel_selection"] = SelectionActions.CancelSelection,
        ["select_bundle"] = SelectionActions.SelectBundle,
        ["confirm_bundle_selection"] = SelectionActions.ConfirmBundleSelection,
        ["cancel_bundle_selection"] = SelectionActions.CancelBundleSelection,
        ["select_relic"] = SelectionActions.SelectRelic,
        ["skip_relic_selection"] = SelectionActions.SkipRelicSelection,
    };

    public static IReadOnlyCollection<string> Names => Handlers.Keys;

    // Same preconditions as before for every in-run action: a run, a local player, no blocking tutorial or popup.
    public static Dictionary<string, object?> Execute(string action, Dictionary<string, JsonElement> data)
    {
        if (!RunManager.Instance.IsInProgress) return Outcome.Fail("No run in progress");
        var run = RunManager.Instance.DebugOnlyGetState()!;
        var player = LocalContext.GetMe(run);
        if (player == null) return Outcome.Fail("Could not find local player");
        if (MenuActions.BlockingPopupVisible())
            return Outcome.Fail("Blocking popup active. Use menu_select with one of the advertised popup options before gameplay actions.");
        return Handlers.TryGetValue(action, out var handler)
            ? handler(new ActionContext(run, player, data))
            : Outcome.Fail($"Unknown action: {action}");
    }
}
