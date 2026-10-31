using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;

namespace SpireLink.Game;

// A creature's visible powers as the "status" list, and the pets that fight beside a player.
internal static class Powers
{
    // {id, name, amount, type, description, keywords, applier_combat_id, amount_raw, internal_data?, facing?} per visible power.
    // The power's own hover tip supplies the description; the other tips become keywords. A power whose tips fail to resolve
    // mid-transition is skipped rather than failing the whole state.
    public static List<Dictionary<string, object?>> Of(Creature creature)
    {
        var powers = new List<Dictionary<string, object?>>();
        foreach (var power in creature.Powers)
        {
            if (!power.IsVisible) continue;
            try
            {
                string? description = null;
                var extra = new List<IHoverTip>();
                foreach (var tip in power.HoverTips)
                {
                    if (tip.Id != power.Id.ToString()) extra.Add(tip);
                    else if (tip is HoverTip own && own.Description != null) description = Text.Plain(own.Description);
                }
                var entry = new Dictionary<string, object?>
                {
                    ["id"] = power.Id.Entry,
                    ["name"] = Text.Of(() => power.Title),
                    ["amount"] = power.DisplayAmount,
                    ["type"] = power.Type.ToString(),
                    ["description"] = description ?? Text.Of(() => power.SmartDescription),
                    ["keywords"] = Tips.Of(extra)
                };
                try { entry["applier_combat_id"] = power.Applier?.CombatId is uint applier ? (int?)applier : null; } catch { }
                try { entry["amount_raw"] = power.Amount; } catch { }
                try { if (InternalData(power) is { } data) entry["internal_data"] = data; } catch { }
                try { if (power.GetType().GetProperty("Facing")?.GetValue(power) is { } facing) entry["facing"] = facing.ToString(); } catch { }
                powers.Add(entry);
            }
            catch { }
        }
        return powers;
    }

    // The power's private data object (PowerModel._internalData), field by field; null when it has none.
    public static Dictionary<string, object?>? InternalData(PowerModel power) => PrivateData.Fields(Nodes.Field(power, "_internalData"));

    // One field of that data object (an "isReviving" flag); null when the power has no such field.
    public static object? InternalField(PowerModel power, string name)
    {
        var data = Nodes.Field(power, "_internalData");
        return data?.GetType().GetField(name)?.GetValue(data);
    }

    // Pets with real combat state: Osty for the Necrobinder. Cosmetic companions are not reported.
    public static List<Dictionary<string, object?>> Pets(Player player)
    {
        var pets = new List<Dictionary<string, object?>>();
        if (player.Osty is { } osty)
            pets.Add(new()
            {
                ["id"] = osty.Monster?.Id.Entry ?? "OSTY",
                ["name"] = Text.Of(() => osty.Monster?.Title) ?? "Otsy",
                ["alive"] = osty.IsAlive,
                ["hp"] = osty.CurrentHp,
                ["max_hp"] = osty.MaxHp,
                ["block"] = osty.Block,
                ["status"] = Of(osty)
            });
        return pets;
    }
}
