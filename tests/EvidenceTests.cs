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
