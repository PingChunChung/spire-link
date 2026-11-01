using System.Text.Json;
using SpireLink;

int checks = 0;
void Check(bool condition, string why) { checks++; if (!condition) throw new Exception(why); }
using (var cancel = new CancellationTokenSource())
{
    int mutations = 0;
    var invocation = new QueuedInvocation<int>(() => ++mutations, cancel.Token);
    cancel.Cancel();
    invocation.Run();
    Check(invocation.Task.IsCanceled && mutations == 0 && !invocation.Started, "A cancelled queued mutation must never execute later");
}
using (var cancel = new CancellationTokenSource())
{
    int mutations = 0;
    var invocation = new QueuedInvocation<int>(() => { cancel.Cancel(); return ++mutations; }, cancel.Token);
    invocation.Run();
    invocation.Run();
    Check(await invocation.Task == 1 && mutations == 1 && invocation.Started, "Cancellation after dispatch must not interrupt or repeat the mutation");
}
{
    var invocation = new QueuedInvocation<int>(() => throw new InvalidOperationException("partially dispatched"), CancellationToken.None);
    invocation.Run();
    Check(invocation.Task.IsFaulted && invocation.Started, "A running exception must remain distinguishable from queue cancellation");
}
var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
foreach (var status in new[] { "accepted", "unknown", "pending" })
{
    var resolution = new ReceiptResolution();
    var original = new { request_id = "one", status, result = new { status = "ok" }, observation = new { state_version = 1 }, error = new { code = "uncertain" } };
    Check(JsonSerializer.SerializeToElement(resolution.Apply(original, options)).GetProperty("status").GetString() == status,
        "An unresolved receipt cannot claim completion");
    resolution.MarkSettled(3, "new-state", DateTimeOffset.UtcNow);
    resolution.MarkSettled(4, "later-state", DateTimeOffset.UtcNow);
    var updated = JsonSerializer.SerializeToElement(resolution.Apply(original, options));
    Check(updated.GetProperty("status").GetString() == "completed" && updated.GetProperty("initial_status").GetString() == status,
        "A late postcondition updates the same receipt while preserving its original status");
    Check(updated.GetProperty("observation_ref").GetProperty("state_version").GetInt64() == 3 && !updated.TryGetProperty("observation", out _),
        "Late settlement references its first observed state and must not return an old snapshot");
    Check(updated.GetProperty("initial_error").GetProperty("code").GetString() == "uncertain" && !updated.TryGetProperty("error", out _),
        "A resolved uncertainty is preserved as provenance rather than a current error");
}
{
    var resolution = new ReceiptResolution();
    resolution.MarkSettled(2, "x", DateTimeOffset.UtcNow);
    foreach (var status in new[] { "rejected", "completed" })
    {
        var result = JsonSerializer.SerializeToElement(resolution.Apply(new { status }, options));
        Check(result.GetProperty("status").GetString() == status, "Reconciliation cannot override rejected or already completed outcomes");
    }
}
foreach (var valid in new[] { "{}", """{"importance":{"level":"major","reason":"Risky route","capture":true,"tags":["route"]}}""" })
    Check(DecisionMetadata.Validate(JsonDocument.Parse(valid).RootElement) == null, "Optional legacy and structured importance must be accepted");
foreach (var invalid in new[] { "[]", """{"importance":"major"}""", """{"importance":{"level":"unknown","reason":"x"}}""",
    """{"importance":{"level":"major","reason":""}}""", """{"importance":{"level":"major","reason":"x","capture":"true"}}""" })
    Check(DecisionMetadata.Validate(JsonDocument.Parse(invalid).RootElement) != null, "Malformed importance must be rejected before dispatch");
Check(KnowledgeText.NormalizeEnergyIcons("獲得[colorless_energy_icon.png][silent_energy_icon.png]。") == "獲得[energy][energy]。",
    "Template energy count must survive normalization across character contexts");
Check(KnowledgeText.NormalizeEnergyIcons("1[IRONCLAD_ENERGY_ICON.PNG]與[star_icon.png]") == "1[energy]與[star_icon.png]",
    "Only energy icons normalize; star and other resource icons remain distinct");
Check(KnowledgeText.NormalizeEnergyIcons("造成10點傷害。") == "造成10點傷害。",
    "Ordinary effect values must remain unchanged");
// HTTP status of a receipt: unfinished outcomes (accepted, pending) answer 202 on /v1/actions and on receipt lookups.
Check(ReceiptStatus.HttpStatus("completed") == 200 && ReceiptStatus.HttpStatus("unknown") == 200 && ReceiptStatus.HttpStatus(null) == 200, "final outcomes are 200");
Check(ReceiptStatus.HttpStatus("accepted") == 202 && ReceiptStatus.HttpStatus("pending") == 202, "accepted and pending are 202");
Check(ReceiptStatus.HttpStatus("rejected") == 409, "rejected is 409");
Check(ReceiptStatus.Of(JsonSerializer.SerializeToElement(new { status = "accepted" }, options)) == "accepted" &&
      ReceiptStatus.Of(JsonSerializer.SerializeToElement(new { request_id = "x" }, options)) == null, "status is read from the receipt");
Check(ReceiptStatus.IsUnresolved("accepted") && ReceiptStatus.IsUnresolved("pending") && ReceiptStatus.IsUnresolved("unknown") &&
      !ReceiptStatus.IsUnresolved("completed") && !ReceiptStatus.IsUnresolved("rejected"), "unresolved statuses are the ones a late settlement can still update");
{
    // A settled resolution answers 200 for a receipt that was accepted, and the store may then evict it.
    var resolution = new ReceiptResolution();
    var accepted = new { request_id = "late", status = "accepted" };
    Check(!resolution.Settled && ReceiptStatus.HttpStatus(ReceiptStatus.Of(JsonSerializer.SerializeToElement(resolution.Apply(accepted, options), options))) == 202,
        "an unsettled accepted receipt answers 202");
    resolution.MarkSettled(9, "h", DateTimeOffset.UtcNow);
    Check(resolution.Settled && ReceiptStatus.HttpStatus(ReceiptStatus.Of(JsonSerializer.SerializeToElement(resolution.Apply(accepted, options), options))) == 200,
        "a settled receipt answers 200");
}
// data_directory may not be where the game reads mod manifests.
string game = Path.Combine(Path.GetTempPath(), "sts2-game"), mod = Path.Combine(game, "mods", "SpireLink");
Check(DataDirectoryPolicy.Reject(Path.Combine(game, "mods", "SpireLink", "data"), game, mod) != null, "inside mods/ is rejected");
Check(DataDirectoryPolicy.Reject(Path.Combine(game, "mods"), game, mod) != null, "mods/ itself is rejected");
Check(DataDirectoryPolicy.Reject(Path.Combine(game, "MODS", "x"), game, mod) != null, "the check ignores case");
Check(DataDirectoryPolicy.Reject(Path.Combine(game, "mods", "..", "logs"), game, mod) == null, "a path that only passes through mods/ is allowed");
string workshopMod = Path.Combine(Path.GetTempPath(), "Steam", "steamapps", "workshop", "content", "2868840", "123");
Check(DataDirectoryPolicy.Reject(Path.Combine(workshopMod, "data"), game, workshopMod) != null, "inside the Workshop mod folder is rejected");
Check(DataDirectoryPolicy.Reject(Path.Combine(Path.GetTempPath(), "steamapps", "workshop", "content", "other"), game, null) != null, "any Workshop content folder is rejected");
Check(DataDirectoryPolicy.Reject(Path.Combine(game, "modsdata"), game, mod) == null, "a sibling whose name starts with mods is allowed");
Check(DataDirectoryPolicy.Reject(Path.Combine(Path.GetTempPath(), "SpireLink"), game, mod) == null, "the default location is allowed");
Check(DataDirectoryPolicy.Reject(Path.Combine(Path.GetTempPath(), "SpireLink"), null, null) == null, "unknown game and mod folders reject nothing but Workshop paths");
Check(GameVersionCheck.Compatibility("v0.107.1", new[] { "0.107.1" }) == "tested", "The game's v-prefixed version matches a tested version");
Check(GameVersionCheck.Compatibility("v0.111.0", new[] { "0.107.1" }) == "untested_game_version", "A newer game version is reported as untested");
Check(GameVersionCheck.Compatibility(null, new[] { "0.107.1" }) == "unknown_game_version", "A missing game version is never reported as tested");
Console.WriteLine($"Runtime tests passed: {checks} assertions");
