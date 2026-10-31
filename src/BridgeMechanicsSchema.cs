namespace SpireLink;

// Pure observation DTOs. A missing value is null, never a numerical zero.
internal sealed record MechanicsCardFact(
    string InstanceId, string ModelType, decimal? DamageBase = null,
    decimal? StranglePowerBase = null, decimal? BlockBase = null,
    IReadOnlyList<string>? ReadIssues = null);

internal sealed record MechanicsPowerFact(
    string InstanceId, string ModelType, string? PowerId,
    object? OwnerCombatId, bool OwnerIsLocalPlayer, bool OwnerJoinComplete,
    object? ApplierCombatId, bool? ApplierIsLocalPlayer,
    int? Amount = null, decimal? SlowStacksBase = null, int? DisplayAmount = null,
    IReadOnlyList<string>? ReadIssues = null, bool ApplierJoinComplete = false);

internal sealed record MechanicsCombatantFact(object ModelReference, object? CombatId,
    bool VisibleJoined);

internal static class BridgeMechanicsSchema
{
    internal const string Schema = "decision_mechanics.v1";
    internal const string VerifiedBuild = "0.1.0+59260271157f76a2896f0eab5bc6ea1245d8b314";
    internal const string VerifiedSha256 = "a1f9e653f1e28e4076558fee1e60d218619cb7e057b887c6417f62c62c6d7a52";

    private const string Strangle = "MegaCrit.Sts2.Core.Models.Cards.Strangle";
    private const string DefendSilent = "MegaCrit.Sts2.Core.Models.Cards.DefendSilent";
    private const string StranglePower = "MegaCrit.Sts2.Core.Models.Powers.StranglePower";
    private const string ShriekPower = "MegaCrit.Sts2.Core.Models.Powers.ShriekPower";
    private const string SlowPower = "MegaCrit.Sts2.Core.Models.Powers.SlowPower";

    internal static (bool? IsLocalPlayer, bool JoinComplete) ResolveApplier(
        object? applier, object localCreature, object? localCombatId,
        object? applierCombatId, IReadOnlyList<MechanicsCombatantFact> currentEnemies)
    {
        if (applier is null || applierCombatId is null) return (null, false);
        if (ReferenceEquals(applier, localCreature))
            return (true, Equals(applierCombatId, localCombatId));
        return (false, currentEnemies.Any(enemy => enemy.VisibleJoined
            && ReferenceEquals(enemy.ModelReference, applier)
            && Equals(enemy.CombatId, applierCombatId)));
    }

    internal static Dictionary<string, object?> Build(
        string? combatEpoch, string? assemblyBuild, string? assemblySha256,
        IReadOnlyList<MechanicsCardFact> cards, IReadOnlyList<MechanicsPowerFact> powers,
        IReadOnlyList<string>? observationIssues = null)
    {
        var compatible = string.Equals(assemblyBuild, VerifiedBuild, StringComparison.Ordinal)
            && string.Equals(assemblySha256, VerifiedSha256, StringComparison.OrdinalIgnoreCase);
        var issues = new List<string>(observationIssues ?? []);
        if (!compatible) issues.Add("game_assembly_unverified");
        if (string.IsNullOrWhiteSpace(combatEpoch)) issues.Add("combat_epoch_missing");
        var cardRows = cards.Select(card => BuildCard(card, compatible)).ToList();
        var powerRows = powers.Select(power => BuildPower(power, compatible)).ToList();
        bool readComplete = issues.Count == 0 && cardRows.All(ReadComplete) && powerRows.All(ReadComplete);
        bool semanticsSupported = compatible && issues.Count == 0
            && cardRows.All(SemanticsSupported) && powerRows.All(SemanticsSupported);
        return new Dictionary<string, object?>
        {
            ["schema"] = Schema,
            ["source"] = "game_model_current_instance",
            ["combat_epoch"] = combatEpoch,
            ["assembly_product_version"] = assemblyBuild,
            ["assembly_sha256"] = assemblySha256,
            ["verified_assembly_product_version"] = VerifiedBuild,
            ["verified_assembly_sha256"] = VerifiedSha256,
            ["adapter_compatible"] = compatible,
            ["completeness"] = Completeness(readComplete, semanticsSupported, false, false),
            ["cards"] = cardRows,
            ["powers"] = powerRows,
            ["issues"] = issues
        };
    }

    private static Dictionary<string, object?> BuildCard(MechanicsCardFact fact, bool compatible)
    {
        var issues = new List<string>(fact.ReadIssues ?? []);
        var values = new Dictionary<string, object?>();
        string adapter = fact.ModelType switch
        {
            Strangle => "strangle_card.v1",
            DefendSilent => "defend_silent_card.v1",
            _ => "unknown"
        };
        if (string.IsNullOrWhiteSpace(fact.InstanceId)) issues.Add("card_instance_id_missing");
        if (adapter == "strangle_card.v1")
        {
            AddValue(values, issues, "damage_base", fact.DamageBase);
            AddValue(values, issues, "strangle_power_base", fact.StranglePowerBase);
        }
        else if (adapter == "defend_silent_card.v1")
            AddValue(values, issues, "block_base", fact.BlockBase);
        else issues.Add("card_semantics_unknown");
        if (!compatible) issues.Add("adapter_assembly_unverified");
        var readComplete = adapter != "unknown" && !issues.Any(IsReadIssue);
        var supported = compatible && adapter != "unknown" && readComplete;
        return new Dictionary<string, object?>
        {
            ["instance_id"] = fact.InstanceId,
            ["model_type"] = fact.ModelType,
            ["adapter"] = adapter,
            ["raw_values"] = values,
            ["semantics"] = supported ? CardSemantics(adapter) : null,
            ["completeness"] = Completeness(readComplete, supported, false, false),
            ["issues"] = issues
        };
    }

    private static Dictionary<string, object?> BuildPower(MechanicsPowerFact fact, bool compatible)
    {
        var issues = new List<string>(fact.ReadIssues ?? []);
        var values = new Dictionary<string, object?>();
        string adapter = fact.ModelType switch
        {
            StranglePower => "strangle_power.v1",
            ShriekPower => "shriek_power.v1",
            SlowPower => "slow_power.v1",
            _ => "unknown"
        };
        if (string.IsNullOrWhiteSpace(fact.InstanceId)) issues.Add("power_instance_id_missing");
        if (fact.OwnerCombatId is null || !fact.OwnerJoinComplete)
            issues.Add("power_owner_join_failed");
        if (adapter is "strangle_power.v1" or "shriek_power.v1")
            AddValue(values, issues, "amount", fact.Amount);
        if (adapter == "slow_power.v1")
        {
            AddValue(values, issues, "slow_stacks_base", fact.SlowStacksBase);
            AddValue(values, issues, "display_amount", fact.DisplayAmount);
        }
        if (adapter == "strangle_power.v1" && !fact.ApplierJoinComplete)
            issues.Add("strangle_applier_join_failed");
        if (adapter == "unknown") issues.Add("power_semantics_unknown");
        if (!compatible) issues.Add("adapter_assembly_unverified");
        var readComplete = adapter != "unknown" && !issues.Any(IsReadIssue);
        var supported = compatible && adapter != "unknown" && readComplete;
        return new Dictionary<string, object?>
        {
            ["instance_id"] = fact.InstanceId,
            ["model_type"] = fact.ModelType,
            ["power_id"] = fact.PowerId,
            ["adapter"] = adapter,
            ["owner_combat_id"] = fact.OwnerCombatId,
            ["owner_is_local_player"] = fact.OwnerIsLocalPlayer,
            ["owner_join_complete"] = fact.OwnerJoinComplete,
            ["applier_combat_id"] = fact.ApplierCombatId,
            ["applier_is_local_player"] = fact.ApplierIsLocalPlayer,
            ["applier_join_complete"] = fact.ApplierJoinComplete,
            ["raw_values"] = values,
            ["semantics"] = supported ? PowerSemantics(adapter) : null,
            ["completeness"] = Completeness(readComplete, supported, false, false),
            ["issues"] = issues
        };
    }

    private static Dictionary<string, object?> CardSemantics(string adapter) => adapter switch
    {
        "strangle_card.v1" => new()
        {
            ["on_play"] = "attack_selected_enemy_then_apply_strangle_power",
            ["attack_amount_from"] = "damage_base",
            ["applied_power_amount_from"] = "strangle_power_base",
            ["new_power_before_card_played_capture"] = false,
            ["installed_power_trigger"] = "before_later_card_played_capture_then_after_card_played_damage_owner_if_card_owner_is_applier_player",
            ["installed_power_damage_value_props"] = new[] { "Unblockable", "Unpowered" },
            ["installed_power_expires"] = "owner_side_turn_end"
        },
        _ => new()
        {
            ["on_play"] = "gain_block_self",
            ["block_amount_from"] = "block_base"
        }
    };

    private static Dictionary<string, object?> PowerSemantics(string adapter) => adapter switch
    {
        "strangle_power.v1" => new()
        {
            ["before_card_played"] = "capture_current_amount_if_card_owner_is_applier_player",
            ["after_card_played"] = "damage_owner_with_captured_amount",
            ["damage_value_props"] = new[] { "Unblockable", "Unpowered" },
            ["expires"] = "owner_side_turn_end"
        },
        "shriek_power.v1" => new()
        {
            ["after_damage_received"] = "if_target_is_owner_and_unblocked_damage_gt_zero_and_post_damage_hp_lte_amount_stun_owner_then_remove_power",
            ["threshold_from"] = "amount"
        },
        _ => new()
        {
            ["after_card_played"] = "increment_slow_stacks_base_by_one",
            ["modify_damage_multiplicative"] = "if_target_is_owner_and_props_is_powered_attack_then_1_plus_0.1_times_slow_stacks_base_else_1",
            ["resets"] = "owner_side_turn_start",
            ["display_amount_is_stacks_times_ten"] = true
        }
    };

    private static void AddValue<T>(Dictionary<string, object?> values, List<string> issues, string key, T? value)
        where T : struct
    {
        if (value.HasValue) values[key] = value.Value;
        else issues.Add($"{key}_missing");
    }

    private static bool IsReadIssue(string issue) => issue.EndsWith("_missing", StringComparison.Ordinal)
        || issue.Contains("read_failed", StringComparison.Ordinal)
        || issue.Contains("join_failed", StringComparison.Ordinal)
        || issue.Contains("unresolved", StringComparison.Ordinal)
        || issue.Contains("type_mismatch", StringComparison.Ordinal);

    private static bool ReadComplete(Dictionary<string, object?> row) =>
        ((Dictionary<string, object?>)row["completeness"]!)["read_complete"] is true;
    private static bool SemanticsSupported(Dictionary<string, object?> row) =>
        ((Dictionary<string, object?>)row["completeness"]!)["semantics_supported"] is true;

    private static Dictionary<string, object?> Completeness(bool read, bool supported, bool interactions, bool effective) => new()
    {
        ["read_complete"] = read,
        ["semantics_supported"] = supported,
        ["interactions_complete"] = interactions,
        ["effective_amount_known"] = effective
    };
}
