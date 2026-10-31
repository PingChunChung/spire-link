using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpireLink;

public record AgentAction(string ActionId, string Action, Dictionary<string, object?> Arguments, string Label);

public sealed class AgentSnapshot
{
    public long Version { get; }
    public string StateHash { get; }
    public JsonElement State { get; }
    public IReadOnlyList<AgentAction> Actions { get; }
    public bool Ready { get; }
    public string StateType { get; }
    public DateTimeOffset ObservedAt { get; }

    public AgentSnapshot(long version, string stateHash, JsonElement state, IReadOnlyList<AgentAction> actions,
        bool ready, string stateType, DateTimeOffset observedAt)
    {
        Version = version;
        StateHash = stateHash;
        State = state.Clone();
        Actions = actions.Select(a => a with { Arguments = new Dictionary<string, object?>(a.Arguments) }).ToArray();
        Ready = ready;
        StateType = stateType;
        ObservedAt = observedAt;
    }

    internal AgentSnapshot Copy() => new(Version, StateHash, State, Actions, Ready, StateType, ObservedAt);
}

public sealed class SnapshotStore
{
    private readonly object _gate = new();
    private AgentSnapshot? _current;
    private string? _rawJson;
    private bool _inputReady;

    public AgentSnapshot? Current
    {
        get { lock (_gate) return _current?.Copy(); }
    }

    public AgentSnapshot Publish(Dictionary<string, object?> state, bool ready)
    {
        ArgumentNullException.ThrowIfNull(state);
        var json = JsonSerializer.Serialize(state);
        lock (_gate)
        {
            if (_current != null && _rawJson == json && _inputReady == ready)
                return _current.Copy();

            using var doc = JsonDocument.Parse(json);
            var element = doc.RootElement.Clone();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json + "\n" + ready))).ToLowerInvariant();
            var type = AgentProtocol.String(element, "state_type") ?? "unknown";
            var actions = AgentProtocol.BuildActions(element, ready);
            var effectiveReady = ready && (actions.Count > 0 || type == "game_over");
            _current = new AgentSnapshot((_current?.Version ?? 0) + 1, hash, element,
                actions, effectiveReady, type, DateTimeOffset.UtcNow);
            _rawJson = json;
            _inputReady = ready;
            return _current.Copy();
        }
    }
}

public static class AgentProtocol
{
    public static object Observation(AgentSnapshot s, string detail = "compact")
    {
        ArgumentNullException.ThrowIfNull(s);
        if (detail != "compact" && detail != "full")
            throw new ArgumentException("detail must be compact or full", nameof(detail));
        return new
        {
            api_version = "1",
            state_version = s.Version,
            state_hash = s.StateHash,
            observed_at = s.ObservedAt,
            ready = s.Ready,
            state = detail == "full" ? s.State.Clone() : Compact(s.State),
            actions = s.Actions,
            capabilities = s.Actions.Select(a => a.Action).Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private static JsonElement Compact(JsonElement state)
    {
        var node = JsonNode.Parse(state.GetRawText())!;
        StripKeywords(node);
        if (node is JsonObject root && root["player"] is JsonObject player)
        {
            foreach (var pile in new[] { "draw_pile", "discard_pile", "exhaust_pile" })
                player.Remove(pile);
            var counts = new JsonObject();
            foreach (var pile in new[] { "draw", "discard", "exhaust" })
            {
                var key = pile + "_pile_count";
                if (player[key] != null) counts[pile] = player[key]!.DeepClone();
            }
            player["pile_counts"] = counts;
        }
        return JsonSerializer.SerializeToElement(node);
    }

    private static void StripKeywords(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("keywords");
            foreach (var child in obj.ToArray()) StripKeywords(child.Value);
        }
        else if (node is JsonArray array)
            foreach (var child in array) StripKeywords(child);
    }

    public static IReadOnlyList<AgentAction> BuildActions(JsonElement state, bool ready)
    {
        var actions = new List<AgentAction>();
        if (!ready || state.ValueKind != JsonValueKind.Object) return actions;
        var type = String(state, "state_type") ?? "unknown";
        void Add(string action, string label, params (string Key, object? Value)[] args)
        {
            var dict = args.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            var canonical = JsonSerializer.Serialize(dict.OrderBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(action + "\n" + canonical)))
                .ToLowerInvariant()[..16];
            actions.Add(new AgentAction(id, action, dict, label));
        }
        static int Index(JsonElement item, int fallback) => Int(item, "index") ?? fallback;
        void Indexed(JsonElement parent, string field, string action, string labelField,
            Func<JsonElement, bool>? allowed = null, string arg = "index")
        {
            var i = 0;
            foreach (var item in Array(parent, field))
            {
                if (allowed == null || allowed(item))
                    Add(action, String(item, labelField) ?? action, (arg, Index(item, i)));
                i++;
            }
        }

        switch (type)
        {
            case "menu":
                var screen = String(state, "menu_screen") ?? "";
                if (screen == "character_select" && Has(state, "lobby")) break;
                // Restrict to implemented single-player navigation. Abandon opens
                // the game's own confirmation popup before changing a saved run.
                var allowedNames = screen switch
                {
                    "main" => new HashSet<string> { "singleplayer", "continue", "abandon_run", "quit" },
                    "singleplayer" => new HashSet<string> { "standard", "daily", "custom", "back" },
                    "character_select" => new HashSet<string> { "confirm", "embark", "back" },
                    "timeline" => new HashSet<string> { "back" },
                    "profile_select" => new HashSet<string> { "back" },
                    "tutorial_prompt" => new HashSet<string> { "yes", "no" },
                    "tutorial" => new HashSet<string> { "advance", "proceed" },
                    "popup" => new HashSet<string> { "back", "cancel", "yes", "no", "continue", "ok", "close", "proceed", "advance", "好的", "不了", "確定", "確認", "取消", "繼續", "關閉", "确定", "确认", "继续", "关闭" },
                    _ => new HashSet<string>()
                };
                foreach (var item in Array(state, "options"))
                {
                    var name = item.ValueKind == JsonValueKind.String ? item.GetString() : String(item, "name");
                    if (name == null || !allowedNames.Contains(name.ToLowerInvariant()) || !Enabled(item)) continue;
                    Add("menu_select", name, ("option", name));
                }
                if (screen == "character_select")
                {
                    foreach (var item in Array(state, "options"))
                    {
                        var name = String(item, "name");
                        if (name != null && name != String(state, "selected_character_id") && name != "back" && name != "confirm" && name != "embark"
                            && Enabled(item) && Array(state, "characters").Any(c => String(c, "id") == name))
                            Add("menu_select", String(item, "name") ?? name, ("option", name));
                    }
                }
                break;
            case "game_over":
                if (Array(Get(state, "game_over"), "options").Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "main_menu"))
                    Add("menu_select", "main_menu", ("option", "main_menu"));
                break;
            case "map":
                var next = 0;
                foreach (var node in Array(Get(state, "map"), "next_options"))
                {
                    if (!Enabled(node)) { next++; continue; }
                    var col = Int(node, "col")?.ToString() ?? "?";
                    var row = Int(node, "row")?.ToString() ?? "?";
                    Add("choose_map_node", $"{String(node, "type") ?? "Node"} ({col},{row})", ("index", Index(node, next)));
                    next++;
                }
                break;
            case "monster":
            case "elite":
            case "boss":
                var battle = Get(state, "battle");
                var player = Get(state, "player");
                if (!Bool(battle, "is_play_phase")) break;
                var enemies = Array(battle, "enemies").Where(e => !Has(e, "hp") || (Int(e, "hp") ?? 1) > 0).ToArray();
                foreach (var card in Array(player, "hand"))
                {
                    if (!Bool(card, "can_play") || !Int(card, "index").HasValue) continue;
                    var index = Int(card, "index")!.Value;
                    var name = String(card, "name") ?? "card";
                    var instance = String(card, "instance_id");
                    var targetType = String(card, "target_type");
                    if (targetType == "AnyEnemy")
                    {
                        foreach (var enemy in enemies)
                        {
                            var combatId = Get(enemy, "combat_id");
                            if (combatId.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)) continue;
                            var target = combatId.ToString();
                            if (instance != null) Add("play_card", name + " → " + (String(enemy, "name") ?? target),
                                ("card_index", index), ("target", target), ("card_instance_id", instance));
                            else Add("play_card", name + " → " + (String(enemy, "name") ?? target),
                                ("card_index", index), ("target", target));
                        }
                    }
                    else if (instance != null) Add("play_card", name, ("card_index", index), ("card_instance_id", instance));
                    else Add("play_card", name, ("card_index", index));
                }
                if (Bool(battle, "can_end_turn")) Add("end_turn", "End turn");
                foreach (var potion in Array(player, "potions"))
                {
                    if (!Bool(potion, "can_use") || !Int(potion, "slot").HasValue) continue;
                    var slot = Int(potion, "slot")!.Value;
                    var name = String(potion, "name") ?? "potion";
                    if (String(potion, "target_type") == "AnyEnemy")
                    {
                        foreach (var enemy in enemies)
                        {
                            var combatId = Get(enemy, "combat_id");
                            if (combatId.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                                Add("use_potion", name + " → " + (String(enemy, "name") ?? combatId.ToString()),
                                    ("slot", slot), ("target", combatId.ToString()));
                        }
                    }
                    else Add("use_potion", name, ("slot", slot));
                }
                break;
            case "event":
                var ev = Get(state, "event");
                if (Bool(ev, "in_dialogue")) { Add("advance_dialogue", "Advance dialogue"); break; }
                Indexed(ev, "options", "choose_event_option", "title", x => Bool(x, "actionable") && !Bool(x, "is_locked") && !Bool(x, "was_chosen"));
                break;
            case "rest_site":
                var rest = Get(state, "rest_site");
                Indexed(rest, "options", "choose_rest_option", "name", x => Bool(x, "is_enabled"));
                if (Bool(rest, "can_proceed")) Add("proceed", "Proceed");
                break;
            case "rewards":
                var rewards = Get(state, "rewards");
                var potionSlotsFull = PotionSlotsKnownFull(Get(state, "player"));
                Indexed(rewards, "items", "claim_reward", "description", x =>
                    Enabled(x) && !(String(x, "type") == "potion" && potionSlotsFull));
                if (Bool(rewards, "can_proceed")) Add("proceed", "Proceed");
                break;
            case "card_reward":
                var reward = Get(state, "card_reward");
                Indexed(reward, "cards", "select_card_reward", "name", x => Bool(x, "actionable"), "card_index");
                if (Bool(reward, "can_skip")) Add("skip_card_reward", "Skip card reward");
                if (Bool(reward, "can_reroll")) Add("reroll_card_reward", "Reroll card reward");
                break;
            case "card_select":
                var select = Get(state, "card_select");
                if (!Bool(select, "preview_showing")) Indexed(select, "cards", "select_card", "name", x => Bool(x, "actionable"));
                if (Bool(select, "can_confirm")) Add("confirm_selection", "Confirm selection");
                if (Bool(select, "can_cancel")) Add("cancel_selection", "Cancel selection");
                break;
            case "bundle_select":
                var bundle = Get(state, "bundle_select");
                if (!Bool(bundle, "preview_showing"))
                {
                    var bundleNumber = 0;
                    foreach (var option in Array(bundle, "bundles"))
                    {
                        if (Bool(option, "actionable")) Add("select_bundle", $"Bundle {Index(option, bundleNumber)}", ("index", Index(option, bundleNumber)));
                        bundleNumber++;
                    }
                }
                if (Bool(bundle, "can_confirm")) Add("confirm_bundle_selection", "Confirm bundle");
                if (Bool(bundle, "can_cancel")) Add("cancel_bundle_selection", "Cancel bundle");
                break;
            case "hand_select":
                var hand = Get(state, "hand_select");
                Indexed(hand, "cards", "combat_select_card", "name", x => Bool(x, "actionable"), "card_index");
                if (Bool(hand, "can_confirm")) Add("combat_confirm_selection", "Confirm selection");
                break;
            case "relic_select":
                var relic = Get(state, "relic_select");
                Indexed(relic, "relics", "select_relic", "name", x => Bool(x, "actionable"));
                if (Bool(relic, "can_skip")) Add("skip_relic_selection", "Skip relic");
                break;
            case "crystal_sphere":
                var sphere = Get(state, "crystal_sphere");
                if (Bool(sphere, "can_use_big_tool") && String(sphere, "tool") != "big") Add("crystal_sphere_set_tool", "Big divination", ("tool", "big"));
                if (Bool(sphere, "can_use_small_tool") && String(sphere, "tool") != "small") Add("crystal_sphere_set_tool", "Small divination", ("tool", "small"));
                foreach (var cell in Array(sphere, "clickable_cells"))
                {
                    if (Int(cell, "x") is int x && Int(cell, "y") is int y)
                        Add("crystal_sphere_click_cell", $"Cell ({x},{y})", ("x", x), ("y", y));
                }
                if (Bool(sphere, "can_proceed")) Add("crystal_sphere_proceed", "Proceed");
                break;
            case "shop":
            case "fake_merchant":
                var shop = type == "shop" ? Get(state, "shop") : Get(Get(state, "fake_merchant"), "shop");
                var inventoryOpen = Bool(shop, "inventory_open");
                if (Bool(shop, "can_open_inventory")) Add("open_shop", "Open shop");
                if (Bool(shop, "can_close_inventory")) Add("close_shop", "Close shop");
                var itemNumber = 0;
                foreach (var item in Array(shop, "items"))
                {
                    if (inventoryOpen && Bool(item, "is_stocked") && Bool(item, "can_afford"))
                    {
                        var name = String(item, "card_name") ?? String(item, "relic_name") ??
                            String(item, "potion_name") ?? String(item, "category") ?? "item";
                        var price = Int(item, "price")?.ToString() ?? "?";
                        Add("shop_purchase", $"{name} ({price} gold)", ("index", Index(item, itemNumber)));
                    }
                    itemNumber++;
                }
                if (!inventoryOpen && Bool(shop, "can_proceed")) Add("proceed", "Proceed");
                break;
            case "treasure":
                var treasure = Get(state, "treasure");
                if (Bool(treasure, "can_open_chest")) Add("open_chest", "Open chest");
                else
                {
                    Indexed(treasure, "relics", "claim_treasure_relic", "name", x => Enabled(x));
                    if (Bool(treasure, "can_proceed")) Add("proceed", "Proceed");
                }
                break;
        }
        // Potion inventory remains actionable on ordinary run screens. Do not
        // bypass selection overlays, unsupported screens, or enemy resolution.
        bool nonCombatRoom = (type is "map" or "event" or "rest_site" or "shop" or "fake_merchant"
            or "rewards" or "treasure") && !(type == "event" && Bool(Get(state, "event"), "in_dialogue"));
        bool combatPlay = (type is "monster" or "elite" or "boss") && Bool(Get(state, "battle"), "is_play_phase");
        if (nonCombatRoom || combatPlay)
            foreach (var potion in Array(Get(state, "player"), "potions"))
            {
                if (Int(potion, "slot") is not int slot || slot < 0) continue;
                var name = String(potion, "name") ?? "potion";
                if (Bool(potion, "can_discard")) Add("discard_potion", "Discard " + name, ("slot", slot));
                if (nonCombatRoom && Bool(potion, "can_use")
                    && String(potion, "target_type") is "None" or "Self" or "AnyAlly" or "AnyPlayer")
                    Add("use_potion", name, ("slot", slot));
            }
        return actions;
    }

    internal static JsonElement Get(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) ? value : default;
    private static bool Has(JsonElement obj, string key) => Get(obj, key).ValueKind != JsonValueKind.Undefined;
    internal static string? String(JsonElement obj, string key)
    {
        var value = Get(obj, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private static int? Int(JsonElement obj, string key)
    {
        var value = Get(obj, key);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;
    }
    private static bool Bool(JsonElement obj, string key) => Get(obj, key).ValueKind == JsonValueKind.True;
    private static bool Enabled(JsonElement obj) => Get(obj, "enabled").ValueKind != JsonValueKind.False &&
        Get(obj, "is_enabled").ValueKind != JsonValueKind.False &&
        Get(obj, "is_locked").ValueKind != JsonValueKind.True;
    // Only a complete, consistent snapshot can suppress a potion reward. Unknown
    // capacity remains an advertised candidate; execution checks the live model.
    private static bool PotionSlotsKnownFull(JsonElement player)
    {
        if (player.ValueKind != JsonValueKind.Object ||
            player.EnumerateObject().Count(p => p.NameEquals("max_potion_slots")) != 1 ||
            player.EnumerateObject().Count(p => p.NameEquals("potions")) != 1) return false;
        var capacity = Int(player, "max_potion_slots");
        var potions = Get(player, "potions");
        if (capacity is null or < 0 || potions.ValueKind != JsonValueKind.Array) return false;
        var occupied = new HashSet<int>();
        foreach (var potion in potions.EnumerateArray())
        {
            if (potion.ValueKind != JsonValueKind.Object ||
                potion.EnumerateObject().Count(p => p.NameEquals("slot")) != 1) return false;
            var slot = Int(potion, "slot");
            if (slot is null or < 0 || slot >= capacity || !occupied.Add(slot.Value)) return false;
        }
        return occupied.Count == capacity;
    }
    private static IEnumerable<JsonElement> Array(JsonElement obj, string key)
    {
        var value = Get(obj, key);
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }
}
