using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace SpireLink.Actions;

// What every in-run action handler receives: the run, the local player and the request arguments.
internal sealed record ActionContext(RunState Run, Player Player, Dictionary<string, JsonElement> Data)
{
    public bool Has(string name) => Data.ContainsKey(name);
    public int? Int(string name) => Data.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    public string? String(string name) => Data.TryGetValue(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;
}
