using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;

namespace SpireLink;

public static partial class SpireLinkMod
{
    private sealed record PowerHandle(string Value);
    private static ConditionalWeakTable<PowerModel, PowerHandle> _powerHandles = new();
    private static readonly Lazy<(string? Build, string? Sha256)> _mechanicsAssembly = new(ReadMechanicsAssembly);

    private static (string? Build, string? Sha256) ReadMechanicsAssembly()
    {
        try
        {
            var assembly = typeof(Strangle).Assembly;
            string? build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(assembly.Location)) return (build, null);
            using var file = File.OpenRead(assembly.Location);
            string sha256 = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            return (build, sha256);
        }
        catch { return (null, null); }
    }

    private static void AddDecisionMechanics(Dictionary<string, object?> state, Player player)
    {
        if (state.GetValueOrDefault("battle") is not Dictionary<string, object?> battle
            || battle.GetValueOrDefault("enemies") is not List<Dictionary<string, object?>> enemyRows
            || state.GetValueOrDefault("player") is not Dictionary<string, object?> playerRow
            || player.PlayerCombatState is not { } combat)
            return;

        var issues = new List<string>();
        var cardFacts = new List<MechanicsCardFact>();
        var powerFacts = new List<MechanicsPowerFact>();
        var hand = combat.Hand.Cards;
        var handRows = playerRow.GetValueOrDefault("hand") as List<Dictionary<string, object?>>;
        if (handRows == null || handRows.Count != hand.Count)
            issues.Add("hand_instance_join_failed");
        for (int i = 0; i < hand.Count; i++)
        {
            var card = hand[i];
            var instanceId = Handle(card);
            if (handRows == null || i >= handRows.Count
                || !string.Equals(handRows[i].GetValueOrDefault("instance_id") as string,
                    instanceId, StringComparison.Ordinal))
                issues.Add("hand_instance_join_failed");
            cardFacts.Add(ReadCardFact(card, instanceId));
        }

        var visibleEnemyIds = new HashSet<object>();
        foreach (var row in enemyRows)
            if (row.GetValueOrDefault("combat_id") is { } id && !visibleEnemyIds.Add(id))
                issues.Add("duplicate_enemy_combat_id");
        var battleCombat = player.Creature.CombatState;
        if (battleCombat == null) issues.Add("combat_state_missing");
        var currentEnemies = battleCombat?.Enemies.Select(enemy => new MechanicsCombatantFact(
            enemy, enemy.CombatId,
            enemy.IsAlive && enemy.CombatId is { } id && visibleEnemyIds.Contains(id))).ToList()
            ?? new List<MechanicsCombatantFact>();
        foreach (var enemy in battleCombat?.Enemies.Where(enemy => enemy.IsAlive) ?? [])
        {
            bool joined = enemy.CombatId is { } enemyId && visibleEnemyIds.Contains(enemyId);
            if (!joined) issues.Add("enemy_combat_id_join_failed");
            ReadPowers(enemy.Powers, enemy, false, joined, player, currentEnemies, powerFacts, issues);
        }
        // Local player's installed powers can modify the same card play.
        ReadPowers(player.Creature.Powers, player.Creature, true, true,
            player, currentEnemies, powerFacts, issues);

        var assembly = _mechanicsAssembly.Value;
        state["decision_mechanics"] = BridgeMechanicsSchema.Build(_combatEpoch,
            assembly.Build, assembly.Sha256, cardFacts, powerFacts, issues.Distinct().ToList());
    }

    private static MechanicsCardFact ReadCardFact(CardModel card,
        string instanceId)
    {
        var modelType = card.GetType().FullName ?? card.GetType().Name;
        var issues = new List<string>();
        decimal? damage = null, strangle = null, block = null;
        try
        {
            if (card.GetType() == typeof(Strangle))
            {
                var vars = card.DynamicVars;
                damage = ReadBase<DamageVar>(vars, "Damage", issues);
                strangle = ReadBase<PowerVar<StranglePower>>(vars, "StranglePower", issues);
            }
            else if (card.GetType() == typeof(DefendSilent))
                block = ReadBase<BlockVar>(card.DynamicVars, "Block", issues);
        }
        catch (Exception ex) { issues.Add("card_value_read_failed:" + ex.GetType().Name); }
        return new MechanicsCardFact(instanceId, modelType, damage, strangle, block, issues);
    }

    private static decimal? ReadBase<T>(DynamicVarSet vars, string key, List<string> issues)
        where T : DynamicVar
    {
        if (!vars.TryGetValue(key, out var value))
        {
            issues.Add(key + "_missing");
            return null;
        }
        if (value is not T)
        {
            issues.Add(key + "_type_mismatch");
            return null;
        }
        return value.BaseValue;
    }

    private static void ReadPowers(IEnumerable<PowerModel> powers, Creature expectedOwner,
        bool ownerIsLocalPlayer, bool ownerJoined, Player player,
        IReadOnlyList<MechanicsCombatantFact> currentEnemies,
        List<MechanicsPowerFact> facts, List<string> observationIssues)
    {
        foreach (var power in powers)
        {
            try
            {
                if (!power.IsVisible)
                {
                    observationIssues.Add("invisible_power_not_enumerated");
                    continue;
                }
            }
            catch (Exception ex)
            {
                observationIssues.Add("power_visibility_read_failed:" + ex.GetType().Name);
                continue;
            }
            var readIssues = new List<string>();
            string modelType = power.GetType().FullName ?? power.GetType().Name;
            string? powerId = null;
            int? amount = null, display = null;
            decimal? slowStacks = null;
            object? actualOwnerId = null, applierId = null;
            bool? applierIsLocal = null;
            bool exactOwnerJoin = false, applierJoinComplete = false;
            try { powerId = power.Id.Entry; }
            catch (Exception ex) { readIssues.Add("power_id_read_failed:" + ex.GetType().Name); }
            try
            {
                var actualOwner = power.Owner;
                actualOwnerId = actualOwner.CombatId;
                exactOwnerJoin = ownerJoined && ReferenceEquals(actualOwner, expectedOwner)
                    && Equals(actualOwnerId, expectedOwner.CombatId);
            }
            catch (Exception ex) { readIssues.Add("power_owner_read_failed:" + ex.GetType().Name); }
            try
            {
                if (power.GetType() == typeof(StranglePower))
                {
                    amount = power.Amount;
                    var applier = power.Applier;
                    if (applier != null)
                    {
                        applierId = applier.CombatId;
                        (applierIsLocal, applierJoinComplete) = BridgeMechanicsSchema.ResolveApplier(
                            applier, player.Creature, player.Creature.CombatId, applierId, currentEnemies);
                    }
                    else readIssues.Add("strangle_applier_unresolved");
                }
                else if (power.GetType() == typeof(ShriekPower))
                {
                    amount = power.Amount;
                    if (power.Owner.Monster is not TerrorEel)
                        readIssues.Add("shriek_owner_type_mismatch");
                }
                else if (power.GetType() == typeof(SlowPower))
                {
                    slowStacks = ReadBase<DynamicVar>(power.DynamicVars, "SlowAmount", readIssues);
                    display = power.DisplayAmount;
                }
            }
            catch (Exception ex) { readIssues.Add("power_value_read_failed:" + ex.GetType().Name); }
            string instanceId = _powerHandles.GetValue(power,
                _ => new PowerHandle($"{_combatEpoch ?? "preview"}:power-{Guid.NewGuid():N}")).Value;
            facts.Add(new MechanicsPowerFact(instanceId, modelType, powerId,
                actualOwnerId, ownerIsLocalPlayer, exactOwnerJoin,
                applierId, applierIsLocal, amount, slowStacks, display, readIssues,
                applierJoinComplete));
        }
    }
}
