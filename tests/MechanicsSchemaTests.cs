using SpireLink;

static class Program
{
    private const string Strangle = "MegaCrit.Sts2.Core.Models.Cards.Strangle";
    private const string Defend = "MegaCrit.Sts2.Core.Models.Cards.DefendSilent";
    private const string StranglePower = "MegaCrit.Sts2.Core.Models.Powers.StranglePower";
    private const string Shriek = "MegaCrit.Sts2.Core.Models.Powers.ShriekPower";
    private const string Slow = "MegaCrit.Sts2.Core.Models.Powers.SlowPower";

    static int Main()
    {
        ValuesKeepTheirBasisAndInstances();
        MissingAndWrongTypesFailClosed();
        UnsupportedTypesAndOwnerJoinFailClosed();
        ApplierMustJoinCurrentCombatByReferenceAndId();
        AssemblyChangeDisablesSemantics();
        Console.WriteLine("Mechanics schema tests passed");
        return 0;
    }

    private static void ValuesKeepTheirBasisAndInstances()
    {
        var state = Build(
            [new("card-a", Strangle, DamageBase: 10m, StranglePowerBase: 3m),
             new("card-b", Defend, BlockBase: 8m)],
            [new("power-a", StranglePower, "STRANGLE_POWER", 17, false, true, 1, true,
                 Amount: 2, ApplierJoinComplete: true),
             new("power-b", StranglePower, "STRANGLE_POWER", 17, false, true, 1, true,
                 Amount: 5, ApplierJoinComplete: true),
             new("power-c", Shriek, "SHRIEK_POWER", 17, false, true, null, null, Amount: 62),
             new("power-d", Slow, "SLOW_POWER", 17, false, true, null, null,
                 SlowStacksBase: 4m, DisplayAmount: 40)]);
        var cards = Cards(state);
        var powers = Powers(state);
        Check(cards.Count == 2 && powers.Count == 4, "Each model instance must survive projection");
        Check((decimal)Values(cards[0])["damage_base"]! == 10m, "Strangle damage uses base");
        Check((decimal)Values(cards[0])["strangle_power_base"]! == 3m, "Strangle amount uses base");
        Check((decimal)Values(cards[1])["block_base"]! == 8m, "Defend block uses base");
        Check((int)Values(powers[0])["amount"]! == 2 && (int)Values(powers[1])["amount"]! == 5,
            "Distinct Strangle instances must not be merged by power ID");
        Check((int)Values(powers[2])["amount"]! == 62, "Shriek threshold comes from current power amount");
        Check((decimal)Values(powers[3])["slow_stacks_base"]! == 4m
            && (int)Values(powers[3])["display_amount"]! == 40,
            "Slow stacks and display amount remain distinct");
        Check(Complete(state, "read_complete") && Complete(state, "semantics_supported"),
            "Verified known values have readable supported semantics");
        Check(!Complete(state, "interactions_complete") && !Complete(state, "effective_amount_known"),
            "A partial mechanics adapter cannot certify final effects");
        Check(Equals(cards[0]["instance_id"], "card-a") && Equals(powers[0]["owner_combat_id"], 17),
            "Opaque card ID and exact owner combat ID survive projection");
    }

    private static void MissingAndWrongTypesFailClosed()
    {
        var state = Build(
            [new("card-a", Strangle, DamageBase: 0m),
             new("card-b", Defend, BlockBase: 8m, ReadIssues: ["Block_type_mismatch"])],
            [new("power-a", Slow, "SLOW_POWER", 17, false, true, null, null,
                DisplayAmount: 30)]);
        var cards = Cards(state);
        var powers = Powers(state);
        Check(Values(cards[0]).ContainsKey("damage_base") && (decimal)Values(cards[0])["damage_base"]! == 0m,
            "A real zero remains a value");
        Check(!Values(cards[0]).ContainsKey("strangle_power_base"), "Missing base must not become zero");
        Check(!Values(powers[0]).ContainsKey("slow_stacks_base")
            && (int)Values(powers[0])["display_amount"]! == 30,
            "Display must not substitute for missing Slow base");
        Check(!Complete(cards[1], "read_complete"), "Wrong runtime variable type fails read completeness");
        Check(!Complete(state, "read_complete") && !Complete(state, "semantics_supported"),
            "Missing and wrong typed values fail the observation");
    }

    private static void UnsupportedTypesAndOwnerJoinFailClosed()
    {
        var state = Build(
            [new("card-x", "OtherNamespace.Strangle", DamageBase: 99m)],
            [new("power-x", "OtherNamespace.SlowPower", "SLOW_POWER", 17, false, true, null, null),
             new("power-y", StranglePower, "STRANGLE_POWER", 21, false, false, 1, true, Amount: 2),
             new("power-z", StranglePower, "STRANGLE_POWER", 17, false, true, null, null, Amount: 2)]);
        Check(Equals(Cards(state)[0]["adapter"], "unknown")
            && Equals(Powers(state)[0]["adapter"], "unknown"),
            "Exact CLR model type is required; matching suffix is insufficient");
        Check(!Complete(Cards(state)[0], "read_complete")
            && !Complete(Powers(state)[0], "read_complete")
            && !Complete(state, "read_complete"),
            "Unknown models with empty raw values cannot claim read completeness");
        Check(!Complete(Powers(state)[1], "read_complete")
            && !Complete(Powers(state)[2], "read_complete"),
            "Owner join failure and unresolved applier are explicit");
        Check(Powers(state).Count == 3 && !Complete(state, "semantics_supported"),
            "Unknown powers remain listed and make global semantics unsupported");
    }

    private static void ApplierMustJoinCurrentCombatByReferenceAndId()
    {
        var local = new object();
        var currentEnemy = new object();
        var foreign = new object();
        var visibleEnemies = new[] { new MechanicsCombatantFact(currentEnemy, 17, true) };
        var missing = BridgeMechanicsSchema.ResolveApplier(null, local, 1, null, visibleEnemies);
        var stale = BridgeMechanicsSchema.ResolveApplier(foreign, local, 1, 17, visibleEnemies);
        var wrongId = BridgeMechanicsSchema.ResolveApplier(currentEnemy, local, 1, 18, visibleEnemies);
        var current = BridgeMechanicsSchema.ResolveApplier(currentEnemy, local, 1, 17, visibleEnemies);
        var localJoined = BridgeMechanicsSchema.ResolveApplier(local, local, 1, 1, visibleEnemies);
        Check(missing.IsLocalPlayer is null && !missing.JoinComplete,
            "Missing applier must remain unresolved");
        Check(stale.IsLocalPlayer is false && !stale.JoinComplete,
            "Foreign creature with a colliding CombatId cannot join");
        Check(wrongId.IsLocalPlayer is false && !wrongId.JoinComplete,
            "Current enemy reference with an inconsistent CombatId cannot join");
        Check(current.IsLocalPlayer is false && current.JoinComplete,
            "A current visible enemy applier can join by reference and CombatId");
        Check(localJoined.IsLocalPlayer is true && localJoined.JoinComplete,
            "The current local player applier can join by reference and CombatId");
        var unresolved = Build([], [new("power-a", StranglePower, "STRANGLE_POWER", 17,
            false, true, 17, stale.IsLocalPlayer, Amount: 2,
            ApplierJoinComplete: stale.JoinComplete)]);
        var resolved = Build([], [new("power-b", StranglePower, "STRANGLE_POWER", 17,
            false, true, 17, current.IsLocalPlayer, Amount: 2,
            ApplierJoinComplete: current.JoinComplete)]);
        Check(!Complete(Powers(unresolved)[0], "read_complete")
            && !Complete(Powers(unresolved)[0], "semantics_supported")
            && Powers(unresolved)[0]["applier_join_complete"] is false,
            "A false nonlocal classification cannot masquerade as a resolved applier");
        Check(Complete(Powers(resolved)[0], "read_complete")
            && Complete(Powers(resolved)[0], "semantics_supported")
            && Powers(resolved)[0]["applier_join_complete"] is true,
            "A joined current enemy applier retains supported timing semantics");
    }

    private static void AssemblyChangeDisablesSemantics()
    {
        var state = BridgeMechanicsSchema.Build("epoch", BridgeMechanicsSchema.VerifiedBuild,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            [new("card-a", Strangle, 10m, 2m)], []);
        Check(state["adapter_compatible"] is false, "ProductVersion alone cannot authorize the adapter");
        Check(Cards(state)[0]["semantics"] is null && !Complete(state, "semantics_supported"),
            "Raw values under a different DLL hash have unknown semantics");
        Check(!Complete(state, "read_complete"), "Unverified assembly is incomplete overall");
    }

    private static Dictionary<string, object?> Build(
        IReadOnlyList<MechanicsCardFact> cards, IReadOnlyList<MechanicsPowerFact> powers) =>
        BridgeMechanicsSchema.Build("epoch", BridgeMechanicsSchema.VerifiedBuild,
            BridgeMechanicsSchema.VerifiedSha256, cards, powers);

    private static List<Dictionary<string, object?>> Cards(Dictionary<string, object?> value) =>
        (List<Dictionary<string, object?>>)value["cards"]!;
    private static List<Dictionary<string, object?>> Powers(Dictionary<string, object?> value) =>
        (List<Dictionary<string, object?>>)value["powers"]!;
    private static Dictionary<string, object?> Values(Dictionary<string, object?> value) =>
        (Dictionary<string, object?>)value["raw_values"]!;
    private static bool Complete(Dictionary<string, object?> value, string key) =>
        ((Dictionary<string, object?>)value["completeness"]!)[key] is true;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
