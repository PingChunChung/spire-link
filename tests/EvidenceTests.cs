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

    if (journal.QueuedStateVersions != 0)
        throw new InvalidOperationException($"Written state versions must leave the in-flight set; {journal.QueuedStateVersions} remain.");

    Console.WriteLine("Evidence tests passed: failed state write can be retried safely.");

    // The writer queue is bounded: with a capacity of 2 and a writer that never drains it (a stalled disk), the third and
    // later events are dropped and counted; Record and SaveState return at once. The drops reach the error output as at
    // most one summary line per interval (plus the final one at dispose), not one line per drop.
    var originalError = Console.Error;
    var errorOutput = new StringWriter();
    Console.SetError(errorOutput);
    try
    {
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
            if (small.QueuedStateVersions != 1)
                throw new InvalidOperationException($"Only the queued state versions stay in the in-flight set, not the dropped ones: {small.QueuedStateVersions}.");
        }
    }
    finally
    {
        Console.SetError(originalError);
    }
    var errorLines = errorOutput.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.Contains("[Spire Link evidence]")).ToList();
    if (errorLines.Count is < 1 or > 2 || !errorLines[^1].Contains("queue is full", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"98 drops must produce one or two summary lines, got {errorLines.Count}: {string.Join(" | ", errorLines)}");
    if (errorLines.Count == 1 && !errorLines[0].Contains("98 errors", StringComparison.Ordinal))
        throw new InvalidOperationException("A single summary line must carry the count of all 98 errors: " + errorLines[0]);
    Console.WriteLine("Evidence tests passed: bounded writer queue drops and counts instead of growing; drops are logged as a rate-limited summary.");

    // The error output is written by the background logger only. After Dispose the logger writes the final summary line
    // itself; the disposing thread (the game's shutdown thread) never writes to the error output.
    var recording = new RecordingErrorWriter(blockFirstLine: false);
    var logged = new EvidenceJournal(Path.Combine(testRoot, "logged"), "logged-session", queueCapacity: 2, startWriter: false, errorWriter: recording);
    logged.Record("", new { });
    await WaitUntilAsync(() => recording.Lines.Count == 1, "The logger did not write the first summary line.");
    logged.Record("", new { }); // reported while the logger sleeps out its interval: only the final line can carry it
    var disposeStarted = Stopwatch.GetTimestamp();
    recording.ForbiddenThread = Environment.CurrentManagedThreadId; // a write on this thread while Dispose runs is Dispose writing
    logged.Dispose();
    recording.ForbiddenThread = -1;
    if (Stopwatch.GetElapsedTime(disposeStarted) > EvidenceJournal.DisposeErrorLogWait + TimeSpan.FromSeconds(2))
        throw new InvalidOperationException("Dispose with an idle logger must return at once.");
    await WaitUntilAsync(() => recording.Lines.Count == 2, "The final summary line was not written after Dispose.");
    if (!recording.Lines[1].Contains("eventType was empty", StringComparison.Ordinal))
        throw new InvalidOperationException("The final line must carry the error reported before Dispose: " + recording.Lines[1]);
    if (recording.Violations != 0)
        throw new InvalidOperationException("The disposing thread must never write to the error output.");

    // A logger stuck in a blocking error output (a slow Console.Error) does not hold up the shutdown: Dispose waits for it
    // at most DisposeErrorLogWait and returns; the final line stays with the logger.
    var blocking = new RecordingErrorWriter(blockFirstLine: true);
    var stuck = new EvidenceJournal(Path.Combine(testRoot, "stuck"), "stuck-session", queueCapacity: 2, startWriter: false, errorWriter: blocking);
    stuck.Record("", new { });
    await WaitUntilAsync(() => blocking.Entered > 0, "The logger did not reach the error output.");
    stuck.Record("", new { });
    disposeStarted = Stopwatch.GetTimestamp();
    blocking.ForbiddenThread = Environment.CurrentManagedThreadId;
    stuck.Dispose();
    blocking.ForbiddenThread = -1;
    var disposeTook = Stopwatch.GetElapsedTime(disposeStarted);
    if (disposeTook > EvidenceJournal.DisposeErrorLogWait + TimeSpan.FromSeconds(2))
        throw new InvalidOperationException($"Dispose must not wait for a blocked logger beyond its bound; it took {disposeTook}.");
    if (blocking.Lines.Count != 0 || blocking.Violations != 0)
        throw new InvalidOperationException("Dispose must not write the final line itself while the logger is blocked.");
    blocking.Unblock();
    await WaitUntilAsync(() => blocking.Lines.Count == 2, "The blocked logger did not finish its line and the final one once released.");
    Console.WriteLine("Evidence tests passed: Dispose never writes to the error output and returns within its bound when the logger is blocked.");

    // Old session folders are pruned to max_sessions in total, newest first; the current session counts towards the
    // quota but is never deleted, and a foreign folder is never touched.
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
    var current = Path.Combine(sessions, "cur-dddddddddddd");
    int SessionFolders() => Directory.GetDirectories(sessions).Count(d => names.Contains(Path.GetFileName(d)));
    if (EvidenceJournal.PruneSessions(pruneRoot, 0).Count != 0 || SessionFolders() != 4) throw new InvalidOperationException("max_sessions 0 keeps everything.");
    var deleted = EvidenceJournal.PruneSessions(pruneRoot, 2, current);
    if (!deleted.OrderBy(x => x).SequenceEqual(new[] { "mid-bbbbbbbbbbbb", "old-aaaaaaaaaaaa" }) || SessionFolders() != 2)
        throw new InvalidOperationException("max_sessions 2 must leave the current and the newest folder: " + string.Join(",", deleted));
    if (!Directory.Exists(Path.Combine(sessions, "new-cccccccccccc")) || !Directory.Exists(current) || !Directory.Exists(Path.Combine(sessions, "not-a-session")))
        throw new InvalidOperationException("Pruning deleted the newest, the current or a foreign folder.");
    deleted = EvidenceJournal.PruneSessions(pruneRoot, 1, current);
    if (!deleted.SequenceEqual(new[] { "new-cccccccccccc" }) || SessionFolders() != 1 || !Directory.Exists(current))
        throw new InvalidOperationException("max_sessions 1 must leave only the current session: " + string.Join(",", deleted));
    if (EvidenceJournal.PruneSessions(pruneRoot, 1, current).Count != 0 || EvidenceJournal.PruneSessions(pruneRoot, 1).Count != 0 || SessionFolders() != 1)
        throw new InvalidOperationException("Pruning within the quota deletes nothing.");
    if (EvidenceJournal.PruneSessions(Path.Combine(testRoot, "absent"), 1).Count != 0) throw new InvalidOperationException("A missing root prunes nothing.");
    Console.WriteLine("Evidence tests passed: session folders are pruned to max_sessions in total, the current one included.");
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

// An error output for the logger: records every line, counts writes made on ForbiddenThread (the thread inside Dispose),
// and optionally blocks the first WriteLine until Unblock, like a Console.Error nobody drains.
sealed class RecordingErrorWriter(bool blockFirstLine) : TextWriter
{
    private readonly ManualResetEventSlim _gate = new(!blockFirstLine);
    private int _entered;
    private int _violations;
    private int _forbiddenThread = -1;
    public List<string> Lines { get; } = new();
    public int Entered => Volatile.Read(ref _entered);
    public int Violations => Volatile.Read(ref _violations);
    public int ForbiddenThread { set => Volatile.Write(ref _forbiddenThread, value); }
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    public void Unblock() => _gate.Set();
    public override void WriteLine(string? value)
    {
        if (Environment.CurrentManagedThreadId == Volatile.Read(ref _forbiddenThread)) Interlocked.Increment(ref _violations);
        Interlocked.Increment(ref _entered);
        _gate.Wait();
        lock (Lines) Lines.Add(value ?? "");
    }
}
