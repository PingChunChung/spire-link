using System.Text.Json;
using SpireLink;

static class Tests
{
    private static int _count;
    private static void Check(bool condition, string message)
    {
        _count++;
        if (!condition) throw new Exception(message);
    }

    private static Dictionary<string, object?> State(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;
    private static IReadOnlyList<AgentAction> Actions(string json, bool ready = true) =>
        AgentProtocol.BuildActions(JsonSerializer.Deserialize<JsonElement>(json), ready);
    private static JsonElement Observation(AgentSnapshot s, string detail = "compact") =>
        JsonSerializer.SerializeToElement(AgentProtocol.Observation(s, detail),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

    public static void Main()
    {
        var store = new SnapshotStore();
        var input = State("""{"state_type":"unknown","value":1}""");
        var first = store.Publish(input, true);
        input["value"] = 2;
        Check(first.State.GetProperty("value").GetInt32() == 1, "published JSON must be detached from source");
        Check(store.Publish(State("""{"state_type":"unknown","value":1}"""), true).Version == first.Version,
            "identical state must retain version");
        var second = store.Publish(input, true);
        Check(second.Version == first.Version + 1 && second.StateHash != first.StateHash, "changed state increments version");
        Check(store.Publish(input, false).Version == second.Version + 1, "readiness change increments version");
        Check(store.Current!.State.GetProperty("value").GetInt32() == 2, "current snapshot available");

        var battle = """
        {"state_type":"monster","battle":{"is_play_phase":true,"can_end_turn":true,"enemies":[
            {"combat_id":7,"entity_id":"slug#0","name":"Slug","hp":10},
            {"combat_id":8,"entity_id":"slug#1","name":"Slug","hp":10}]},
         "player":{"hand":[
            {"index":0,"id":"STRIKE","name":"Strike","instance_id":"card-a","target_type":"AnyEnemy","can_play":true},
            {"index":1,"id":"STRIKE","name":"Strike","instance_id":"card-b","target_type":"AnyEnemy","can_play":true},
            {"index":2,"id":"DEFEND","name":"Defend","instance_id":"card-c","target_type":"Self","can_play":false}],
            "potions":[{"slot":0,"name":"Fire","target_type":"AnyEnemy","can_use":true},
                       {"slot":1,"name":"Blocked","can_use":false}]}}
        """;
        var combatActions = Actions(battle);
        var plays = combatActions.Where(x => x.Action == "play_card").ToArray();
        Check(plays.Length == 4 && plays.Select(x => x.ActionId).Distinct().Count() == 4,
            "duplicate cards and targets require distinct action IDs");
        Check(plays.Any(x => (int)x.Arguments["card_index"]! == 1 && (string)x.Arguments["card_instance_id"]! == "card-b"
            && (string)x.Arguments["target"]! == "8"), "card instance and combat ID target must be preserved");
        Check(combatActions.Count(x => x.Action == "use_potion") == 2 && combatActions.Any(x => x.Action == "end_turn"),
            "combat guards must expose valid actions");
        Check(Actions(battle.Replace("\"is_play_phase\":true", "\"is_play_phase\":false")).Count == 0,
            "non-play phase must have no combat actions");

        const string inventory = """
        "player":{"potions":[{"slot":0,"name":"Juice","target_type":"AnyPlayer","can_use":true,"can_discard":true},{"slot":1,"name":"Revival","target_type":"Self","can_use":false,"can_discard":true},{"slot":2,"name":"Fire","target_type":"AnyEnemy","can_use":false,"can_discard":false}]}
        """;
        foreach (var room in new[] { "map", "event", "rest_site", "shop", "fake_merchant", "rewards", "treasure" })
        {
            var roomActions = Actions("{\"state_type\":\"" + room + "\"," + inventory + "}");
            Check(roomActions.Count(x => x.Action == "use_potion") == 1
                && roomActions.Single(x => x.Action == "use_potion").Arguments["slot"]!.Equals(0)
                && roomActions.Count(x => x.Action == "discard_potion") == 2,
                "ordinary noncombat screen retains usable potions and discard choices: " + room);
        }
        foreach (var overlay in new[] { "card_select", "card_reward", "hand_select", "bundle_select", "relic_select", "unsupported", "transition", "game_over" })
            Check(!Actions("{\"state_type\":\"" + overlay + "\"," + inventory + "}")
                    .Any(x => x.Action is "use_potion" or "discard_potion"),
                "potion actions cannot bypass overlays or unsupported screens: " + overlay);
        Check(Actions("{\"state_type\":\"map\"," + inventory + "}", false).Count == 0,
            "unready inventory has no actions");
        Check(!Actions("{\"state_type\":\"map\"," + inventory.Replace("\"can_use\":false", "\"can_use\":true") + "}")
                .Any(x => x.Action == "use_potion" && x.Arguments["slot"]!.Equals(2)),
            "noncombat use cannot target an absent enemy even if stale metadata says usable");
        Check(Actions("{\"state_type\":\"monster\",\"battle\":{\"is_play_phase\":false}," + inventory + "}").Count == 0,
            "discard cannot bypass enemy turn");
        Check(Actions("{\"state_type\":\"monster\",\"battle\":{\"is_play_phase\":true}," + inventory + "}")
                .Count(x => x.Action == "discard_potion") == 2,
            "automatic potions remain discardable during player turn");
        const string rewardItems = "\"items\":[{\"index\":0,\"type\":\"gold\",\"description\":\"Gold\"},{\"index\":1,\"type\":\"potion\",\"description\":\"New potion\"},{\"index\":2,\"type\":\"card\",\"description\":\"Card\"}]";
        static string PotionRewards(string player) => "{\"state_type\":\"rewards\",\"rewards\":{" + rewardItems +
            ",\"can_proceed\":true},\"player\":" + player + "}";
        var fullRewards = Actions(PotionRewards("""{"max_potion_slots":2,"potions":[{"slot":0,"name":"First","can_discard":true},{"slot":1,"name":"Second","can_discard":true}]}"""));
        Check(fullRewards.Where(x => x.Action == "claim_reward").Select(x => (int)x.Arguments["index"]!).SequenceEqual(new[] { 0, 2 })
            && fullRewards.Count(x => x.Action == "discard_potion") == 2
            && fullRewards.Any(x => x.Action == "proceed"),
            "full potion inventory suppresses only potion reward and preserves original mixed reward indexes");
        Check(Actions(PotionRewards("""{"max_potion_slots":2,"potions":[{"slot":1}]}"""))
            .Any(x => x.Action == "claim_reward" && (int)x.Arguments["index"]! == 1),
            "one empty potion slot permits potion reward");
        Check(!Actions(PotionRewards("""{"max_potion_slots":0,"potions":[]}"""))
            .Any(x => x.Action == "claim_reward" && (int)x.Arguments["index"]! == 1),
            "zero capacity is known full");
        foreach (var uncertainPlayer in new[] {
            """{"potions":[{"slot":0}]}""",
            """{"max_potion_slots":-1,"potions":[]}""",
            """{"max_potion_slots":"two","potions":[{"slot":0}]}""",
            """{"max_potion_slots":2,"potions":"invalid"}""",
            """{"max_potion_slots":true,"max_potion_slots":2,"potions":[{"slot":0},{"slot":1}]}""",
            """{"max_potion_slots":2,"max_potion_slots":true,"potions":[{"slot":0},{"slot":1}]}""",
            """{"max_potion_slots":2,"potions":[],"potions":[{"slot":0},{"slot":1}]}""",
            """{"max_potion_slots":2,"potions":[{"slot":0,"slot":1},{"slot":0}]}""",
            """{"max_potion_slots":2,"potions":[{"slot":0},{"slot":1},{"slot":2}]}""",
            """{"max_potion_slots":2,"potions":[{"slot":0},{"slot":0}]}""",
            """{"max_potion_slots":2,"potions":[{"slot":0},{"slot":2}]}""",
            """{"max_potion_slots":2,"potions":[{"slot":0},{"slot":"1"}]}"""
        })
            Check(Actions(PotionRewards(uncertainPlayer))
                .Any(x => x.Action == "claim_reward" && (int)x.Arguments["index"]! == 1),
                "unknown or inconsistent potion capacity must not be inferred full: " + uncertainPlayer);
        Check(Actions("{\"state_type\":\"event\",\"event\":{\"in_dialogue\":true}," + inventory + "}")
                .All(x => x.Action == "advance_dialogue"),
            "dialogue cannot be interrupted by potion actions");

        var map = """{"state_type":"map","map":{"next_options":[{"index":0,"col":1,"type":"Monster","leads_to":[{"col":1,"row":2}]},{"index":1,"col":2,"type":"Elite","leads_to":[{"col":3,"row":2}]},{"index":2,"col":3,"type":"RestSite"}],"nodes":[{"col":1},{"col":2},{"col":3}],"bosses":[{"id":"B"}]}}""";
        Check(Actions(map).Select(x => (int)x.Arguments["index"]!).SequenceEqual(new[] { 0, 1, 2 }),
            "all map next options must be catalogued");
        var mapCompact = Observation(store.Publish(State(map), true));
        Check(mapCompact.GetProperty("state").GetProperty("map").GetProperty("next_options").GetArrayLength() == 3
            && mapCompact.GetProperty("state").GetProperty("map").GetProperty("nodes").GetArrayLength() == 3,
            "compact observation must preserve map and next_options");

        var selections = new[]
        {
            ("""{"state_type":"card_reward","card_reward":{"cards":[{"index":0,"name":"A","actionable":true},{"index":1,"name":"B","actionable":true}],"can_skip":true}}""", "select_card_reward", 2),
            ("""{"state_type":"card_select","card_select":{"screen_type":"choose","cards":[{"index":0,"name":"A","actionable":true},{"index":1,"name":"B","actionable":true}],"can_cancel":true}}""", "select_card", 2),
            ("""{"state_type":"bundle_select","bundle_select":{"bundles":[{"index":0,"actionable":true},{"index":1,"actionable":true}],"preview_showing":false}}""", "select_bundle", 2),
            ("""{"state_type":"hand_select","hand_select":{"cards":[{"index":0,"name":"A","actionable":true},{"index":1,"name":"B","actionable":true}],"can_confirm":true}}""", "combat_select_card", 2),
            ("""{"state_type":"relic_select","relic_select":{"relics":[{"index":0,"name":"A","actionable":true},{"index":1,"name":"B","actionable":true}],"can_skip":true}}""", "select_relic", 2)
        };
        foreach (var (json, action, expected) in selections)
            Check(Actions(json).Count(x => x.Action == action) == expected, "selection phase lost choices: " + action);

        const string rewardCards = "\"cards\":[{\"index\":0,\"name\":\"A\",\"actionable\":true},{\"index\":1,\"name\":\"B\",\"actionable\":true}]";
        static string CardReward(string flags) => "{\"state_type\":\"card_reward\",\"card_reward\":{" + rewardCards + flags + "}}";
        foreach (var (flags, skip, reroll) in new[]
        {
            (",\"can_skip\":true,\"can_reroll\":true", true, true),
            (",\"can_skip\":true,\"can_reroll\":false", true, false),
            (",\"can_skip\":false,\"can_reroll\":true", false, true),
            (",\"can_skip\":false,\"can_reroll\":false", false, false),
            ("", false, false),
            (",\"can_skip\":\"true\",\"can_reroll\":1", false, false),
            (",\"can_skip\":true,\"can_reroll\":null", true, false),
            (",\"can_skip\":null,\"can_reroll\":true", false, true)
        })
        {
            var rewardActions = Actions(CardReward(flags));
            Check(rewardActions.Count(x => x.Action == "select_card_reward") == 2
                && rewardActions.Any(x => x.Action == "skip_card_reward") == skip
                && rewardActions.Any(x => x.Action == "reroll_card_reward") == reroll,
                "card reward choices and independent alternatives follow their own flags: " + flags);
        }
        var bothReward = CardReward(",\"can_skip\":true,\"can_reroll\":true");
        var afterReroll = CardReward(",\"can_skip\":true,\"can_reroll\":false");
        Check(Actions(bothReward, false).Count == 0, "unready card reward has no actions");
        Check(Actions(bothReward).Where(x => x.Action != "reroll_card_reward").Select(x => x.ActionId)
                .SequenceEqual(Actions(afterReroll).Select(x => x.ActionId)),
            "consuming reroll removes only reroll from the action catalog");
        foreach (var detail in new[] { "compact", "full" })
        {
            var observedReward = Observation(store.Publish(State(bothReward), true), detail)
                .GetProperty("state").GetProperty("card_reward");
            Check(observedReward.GetProperty("can_skip").GetBoolean()
                && observedReward.GetProperty("can_reroll").GetBoolean(),
                "observation retains both card reward flags: " + detail);
        }

        var restricted = """{"state_type":"event","event":{"options":[{"index":0,"title":"Open","is_locked":false,"actionable":true},{"index":1,"title":"Locked","is_locked":true},{"index":2,"title":"Chosen","was_chosen":true}]}}""";
        Check(Actions(restricted).Count == 1, "locked and consumed options must not be advertised");
        Check(Actions("""{"state_type":"rest_site","rest_site":{"options":[{"index":0,"is_enabled":false},{"index":1,"is_enabled":true}]}}""").Single().Arguments["index"]!.Equals(1),
            "disabled rest option must not be advertised");
        Check(Actions("""{"state_type":"shop","shop":{"inventory_open":true,"items":[{"index":0,"is_stocked":false,"can_afford":true},{"index":1,"is_stocked":true,"can_afford":false},{"index":2,"is_stocked":true,"can_afford":true}]}}""").Single().Arguments["index"]!.Equals(2),
            "sold out and unaffordable shop items must not be advertised");
        var closedShop = Actions("""{"state_type":"shop","shop":{"inventory_open":false,"can_open_inventory":true,"can_proceed":true,"items":[{"index":0,"is_stocked":true,"can_afford":true}]}}""");
        Check(closedShop.Select(x => x.Action).SequenceEqual(new[] { "open_shop", "proceed" }),
            "closed shop exposes explicit open and proceed, never purchase");
        var openShop = Actions("""{"state_type":"shop","shop":{"inventory_open":true,"can_close_inventory":true,"can_proceed":true,"items":[{"index":0,"is_stocked":true,"can_afford":true}]}}""");
        Check(openShop.Select(x => x.Action).SequenceEqual(new[] { "close_shop", "shop_purchase" }),
            "open shop exposes close and purchase, never implicit proceed");
        var fakeMerchant = Actions("""{"state_type":"fake_merchant","fake_merchant":{"shop":{"inventory_open":false,"can_open_inventory":true,"items":[{"index":0,"is_stocked":true,"can_afford":true}]}}}""");
        Check(fakeMerchant.Count == 1 && fakeMerchant[0].Action == "open_shop",
            "fake merchant follows explicit inventory state");
        Check(Actions("""{"state_type":"menu","menu_screen":"main","options":["singleplayer","multiplayer","abandon_run","quit"]}""")
            .Select(x => (string)x.Arguments["option"]!).SequenceEqual(new[] { "singleplayer", "abandon_run", "quit" }),
            "single-player setup and the game's abandon-confirmation entry are supported; multiplayer is excluded");
        Check(Actions("""{"state_type":"menu","menu_screen":"tutorial_prompt","options":[{"name":"yes","enabled":true},{"name":"no","enabled":true}]}""").Count == 2,
            "tutorial prompt choices must be exposed");
        Check(Actions("""{"state_type":"menu","menu_screen":"popup","options":[{"name":"Cancel","enabled":true},{"name":"Delete Profile","enabled":true}]}""").Count == 1,
            "destructive popup options must not be exposed");
        Check(Actions("""{"state_type":"crystal_sphere","crystal_sphere":{"can_use_big_tool":true,"can_use_small_tool":false,"clickable_cells":[{"x":1,"y":2}],"can_proceed":true}}""")
            .Select(x => x.Action).SequenceEqual(new[] { "crystal_sphere_set_tool", "crystal_sphere_click_cell", "crystal_sphere_proceed" }),
            "crystal sphere controls must follow visible guards");
        Check(Actions("""{"state_type":"treasure","treasure":{"relics":[{"index":0,"name":"Relic"}],"can_proceed":true}}""").Count == 2,
            "treasure choice and proceed must be exposed");
        var closedTreasure = Actions("""{"state_type":"treasure","treasure":{"can_open_chest":true,"relics":[{"index":0,"name":"Stale"}],"can_proceed":true}}""");
        Check(closedTreasure.Count == 1 && closedTreasure[0].Action == "open_chest" && closedTreasure[0].Arguments.Count == 0,
            "closed treasure must require explicit open before claiming");
        Check(Actions("""{"state_type":"unsupported","foo":1}""").Count == 0 && Actions(map, false).Count == 0,
            "unsupported or unready state must have empty catalog");
        var unknown = Observation(store.Publish(State("""{"state_type":"unsupported"}"""), true));
        Check(unknown.GetProperty("capabilities").GetArrayLength() == 0, "unsupported capabilities must be empty");

        var detailed = store.Publish(State("""{"state_type":"map","player":{"draw_pile":[{"name":"A","actionable":true}],"draw_pile_count":1,"hand":[{"description":"Keep","keywords":[{"x":1}]}]},"map":{"next_options":[{"index":0}]}}"""), true);
        var compact = Observation(detailed).GetProperty("state");
        Check(!compact.GetProperty("player").TryGetProperty("draw_pile", out _)
            && !compact.GetProperty("player").GetProperty("hand")[0].TryGetProperty("keywords", out _)
            && compact.GetProperty("player").GetProperty("hand")[0].GetProperty("description").GetString() == "Keep"
            && compact.GetProperty("player").GetProperty("pile_counts").GetProperty("draw").GetInt32() == 1,
            "compact observation must prune piles and keywords while retaining descriptions and counts");
        Check(Observation(detailed, "full").GetProperty("state").GetProperty("player").TryGetProperty("draw_pile", out _),
            "full observation must retain original state");
        Console.WriteLine($"Protocol tests passed: {_count} assertions");
    }
}
