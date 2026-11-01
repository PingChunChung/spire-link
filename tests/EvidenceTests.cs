using System.Diagnostics;
using System.Text.Json;
using SpireLink;

var testRoot = Path.Combine(Path.GetTempPath(), $"sts2-evidence-retry-{Guid.NewGuid():N}");

try
{
    using var journal = new EvidenceJournal(testRoot, "retry-session");
    var statesDirectory = Path.Combine(journal.SessionDirectory, "states");
    await WaitUntilAsync(
        () => Directory.Exists(statesDirectory),
        "Evidence writer did not create the states directory.");

    var statePath = Path.Combine(statesDirectory, "7.json");
    Directory.CreateDirectory(statePath);

    using (var failedState = JsonDocument.Parse("""{"attempt":"failed"}"""))
    {
        journal.SaveState(7, "failed-hash", failedState.RootElement);
    }

    await WaitUntilAsync(
        () => journal.LastError?.Contains("state version 7", StringComparison.OrdinalIgnoreCase) == true,
        "The forced state write failure was not reported.");

    Directory.Delete(statePath);
    using (var retryState = JsonDocument.Parse("""{"attempt":"retry"}"""))
    {
        journal.SaveState(7, "retry-hash", retryState.RootElement);
    }

    journal.Dispose();

    if (!File.Exists(statePath))
    {
        throw new InvalidOperationException("Retry did not persist the previously failed state version.");
    }

    using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
    var root = persisted.RootElement;
    if (root.GetProperty("state_hash").GetString() != "retry-hash"
        || root.GetProperty("state").GetProperty("attempt").GetString() != "retry")
    {
        throw new InvalidOperationException("Retry persisted stale data from the failed attempt.");
    }

    Console.WriteLine("Evidence tests passed: failed state write can be retried safely.");

    // The writer queue is bounded: with a capacity of 2 and a writer that never drains it (a stalled disk), the third and
    // later events are dropped and counted; Record and SaveState return at once.
    using (var small = new EvidenceJournal(Path.Combine(testRoot, "bounded"), "bounded-session", queueCapacity: 2, startWriter: false))
    {
        var started = Stopwatch.GetTimestamp();
        for (int i = 0; i < 50; i++)
        {
            using var doc = JsonDocument.Parse("{\"i\":" + i + "}");
            small.SaveState(1000 + i, "h" + i, doc.RootElement);
            small.Record("filler", new { i });
        }
        if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(2))
            throw new InvalidOperationException("Recording into a full queue must not block.");
        if (small.Dropped != 98) throw new InvalidOperationException($"Expected 98 drops past the capacity of 2, got {small.Dropped}.");
        if (small.LastError?.Contains("queue is full", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidOperationException("A drop must be reported as the last error: " + small.LastError);
    }
    Console.WriteLine("Evidence tests passed: bounded writer queue drops and counts instead of growing.");

    // Old session folders are pruned to max_sessions, newest first, never the current one and never a foreign folder.
    var pruneRoot = Path.Combine(testRoot, "prune");
    var sessions = Path.Combine(pruneRoot, "sessions");
    Directory.CreateDirectory(sessions);
    var names = new[] { "old-aaaaaaaaaaaa", "mid-bbbbbbbbbbbb", "new-cccccccccccc", "cur-dddddddddddd" };
    for (int i = 0; i < names.Length; i++)
    {
        var dir = Directory.CreateDirectory(Path.Combine(sessions, names[i]));
        File.WriteAllText(Path.Combine(dir.FullName, "events.jsonl"), "{}");
        dir.LastWriteTimeUtc = DateTime.UtcNow.AddHours(i - 10);
    }
    Directory.CreateDirectory(Path.Combine(sessions, "not-a-session"));
    if (EvidenceJournal.PruneSessions(pruneRoot, 0).Count != 0) throw new InvalidOperationException("max_sessions 0 keeps everything.");
    var deleted = EvidenceJournal.PruneSessions(pruneRoot, 1, Path.Combine(sessions, "cur-dddddddddddd"));
    if (!deleted.OrderBy(x => x).SequenceEqual(new[] { "mid-bbbbbbbbbbbb", "old-aaaaaaaaaaaa" }))
        throw new InvalidOperationException("Pruning kept the wrong folders: " + string.Join(",", deleted));
    if (!Directory.Exists(Path.Combine(sessions, "new-cccccccccccc")) || !Directory.Exists(Path.Combine(sessions, "cur-dddddddddddd")) || !Directory.Exists(Path.Combine(sessions, "not-a-session")))
        throw new InvalidOperationException("Pruning deleted the newest, the current or a foreign folder.");
    if (EvidenceJournal.PruneSessions(Path.Combine(testRoot, "absent"), 1).Count != 0) throw new InvalidOperationException("A missing root prunes nothing.");
    Console.WriteLine("Evidence tests passed: session folders are pruned to max_sessions.");
}
finally
{
    if (Directory.Exists(testRoot))
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

static async Task WaitUntilAsync(Func<bool> condition, string timeoutMessage)
{
    var started = Stopwatch.GetTimestamp();
    while (!condition())
    {
        if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5))
        {
            throw new TimeoutException(timeoutMessage);
        }

        await Task.Delay(20);
    }
}
