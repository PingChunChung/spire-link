using System.Collections.Concurrent;
using System.Text.Json;
using SpireLink;

int checks = 0;
void Check(bool condition, string reason) { checks++; if (!condition) throw new Exception(reason); }
JsonElement State(string? runId, string type = "menu", int floor = 0, string seed = "same-seed") =>
    JsonSerializer.SerializeToElement(new { state_type = type, context = new { run_id = runId }, run = new { floor, seed } });
var runA = State("live-object-a", "monster", 1);
var runB = State("live-object-b", "monster", 1);
var menu = State(null);
var unknown = JsonSerializer.SerializeToElement(new { state_type = "transition" });
RunCaptureBudget.Lease Reserve(RunCaptureBudget budget, JsonElement state, int limit = 100)
{
    Check(budget.TryReserve(state, limit, out var lease, out _), "Expected capture reservation");
    return lease!;
}

{
    var budget = new RunCaptureBudget();
    budget.Observe(runA);
    var original = Reserve(budget, runA, 1);
    budget.Complete(original, true);
    foreach (var type in new[] { "monster", "rewards", "map", "event", "boss", "game_over" })
    {
        var sameRun = State("live-object-a", type, 50);
        budget.Observe(sameRun);
        Check(!budget.TryReserve(sameRun, 1, out _, out var error) && error == "capture_limit",
            "Scene, act/floor, and repeated state must not renew a run budget");
    }
    budget.Observe(runB);
    var fresh = Reserve(budget, runB, 1);
    Check(fresh.Sequence > original.Sequence, "Same-seed new run gets quota without reusing filenames");
    budget.Complete(fresh, true);
    budget.Observe(runA);
    Check(!budget.TryReserve(runA, 1, out _, out _), "Returning to an already observed identity must not reset usage");
}
{
    var budget = new RunCaptureBudget();
    budget.Observe(runA);
    var failedOld = Reserve(budget, runA, 1);
    budget.Observe(runB);
    var writingNew = Reserve(budget, runB, 1);
    budget.Complete(failedOld, false);
    Check(!budget.TryReserve(runB, 1, out _, out _), "A failed old-run write cannot refund a new-run reservation");
    budget.Complete(writingNew, true);
    budget.Observe(runA);
    budget.Complete(Reserve(budget, runA, 1), true);
}
{
    // Exercise cancellation/disposal races when a background encoder completes
    // while the main thread observes a new run.
    for (int iteration = 0; iteration < 200; iteration++)
    {
        var budget = new RunCaptureBudget();
        budget.Observe(runA);
        if (!budget.TryReserve(runA, 1, out var lease, out _)) throw new Exception("Missing stress-test reservation");
        await Task.WhenAll(Task.Run(() => budget.Observe(runB)), Task.Run(() => budget.Complete(lease!, false)));
        Check(budget.TryReserve(runB, 1, out var fresh, out _), "Observe/completion race cannot leak or debit the new-run reservation");
        budget.Complete(fresh!, true);
    }
}
{
    var budget = new RunCaptureBudget();
    budget.Observe(menu);
    var preRun = Reserve(budget, menu, 1);
    budget.Complete(preRun, true);
    budget.Observe(runA);
    budget.Complete(Reserve(budget, runA, 1), true);
    budget.Observe(menu);
    Check(!budget.TryReserve(menu, 1, out _, out var error) && error == "capture_limit",
        "No-run/menu captures share one bounded scope across returns to menu");
}
{
    var budget = new RunCaptureBudget();
    budget.Observe(runA);
    var late = Reserve(budget, runA, 1);
    budget.Observe(runB);
    Check(late.Token.IsCancellationRequested && !budget.IsCurrent(late), "Changing run cancels in-flight old captures");
    Check(!budget.TryReserve(runA, 1, out _, out var error) && error == "stale_capture_run",
        "Queued old-run captures must be rejected after acquiring CaptureGate");
    budget.Complete(late, true);
    var newRun = Reserve(budget, runB, 1);
    Check(newRun.Sequence != late.Sequence, "A late old-run completion must not reuse the session sequence");
    Check(!budget.TryReserve(runB, 1, out _, out _), "Reservations count against the limit while writes are in flight");
    budget.Complete(newRun, false);
    budget.Complete(Reserve(budget, runB, 1), true);
    budget.Complete(late, false); // Duplicate finalization cannot refund the original retained PNG.
    budget.Observe(runA);
    Check(!budget.TryReserve(runA, 1, out _, out _), "Late completion settles only its original run and is idempotent");
}
{
    var budget = new RunCaptureBudget();
    Check(!budget.TryReserve(menu, 1, out _, out var error) && error == "capture_identity_unknown",
        "Capture must fail closed before any known observation");
    budget.Observe(runA);
    var interrupted = Reserve(budget, runA, 1);
    budget.Observe(unknown);
    Check(interrupted.Token.IsCancellationRequested, "A transition with missing identity cancels pending capture");
    Check(!budget.TryReserve(runA, 1, out _, out error) && error == "capture_identity_unknown",
        "Unknown observation cannot silently acquire a menu or fresh run budget");
    budget.Observe(runA);
    Check(!budget.IsCurrent(interrupted), "An interrupted capture stays invalid even if its run is observed again");
    budget.Complete(interrupted, true);
    Check(!budget.TryReserve(runA, 1, out _, out _), "Unknown transitions cannot renew exhausted run quota");
    foreach (var malformed in new[] { "{}", "{\"context\":{}}", "{\"context\":{\"run_id\":\"\"}}", "{\"context\":{\"run_id\":3}}" })
        Check(RunCaptureBudget.ReadScope(JsonDocument.Parse(malformed).RootElement) == null, "Malformed identity must fail closed");
}
{
    var budget = new RunCaptureBudget();
    budget.Observe(runA);
    var accepted = new ConcurrentBag<RunCaptureBudget.Lease>();
    Parallel.For(0, 500, index => { if (budget.TryReserve(runA, 100, out var lease, out _)) accepted.Add(lease!); });
    Check(accepted.Count == 100, "Concurrent reservations cannot exceed the per-run 100 limit");
    Check(accepted.Select(lease => lease.Sequence).Distinct().Count() == 100, "Concurrent capture names stay unique");
    budget.Observe(runB);
    Parallel.ForEach(accepted, lease => budget.Complete(lease, true));
    var newAccepted = new ConcurrentBag<RunCaptureBudget.Lease>();
    Parallel.For(0, 500, index => { if (budget.TryReserve(runB, 100, out var lease, out _)) newAccepted.Add(lease!); });
    Check(newAccepted.Count == 100, "Late parallel old-run finalization cannot consume any new-run quota");
    Check(!accepted.Select(lease => lease.Sequence).Intersect(newAccepted.Select(lease => lease.Sequence)).Any(),
        "New runs never reset the session-wide capture filename sequence");
    foreach (var lease in newAccepted) budget.Complete(lease, true);
}
{
    var budget = new RunCaptureBudget();
    budget.Observe(menu);
    Check(!budget.TryReserve(menu, 0, out _, out var error) && error == "capture_limit", "Zero configured captures disables menu capture");
    budget.Observe(runA);
    Check(!budget.TryReserve(runA, 0, out _, out _), "Zero configured captures disables run capture");
}
Console.WriteLine($"Capture budget tests passed: {checks} assertions");
