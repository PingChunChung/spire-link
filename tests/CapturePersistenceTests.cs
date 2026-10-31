using System.Security.Cryptography;
using System.Text.Json;
using SpireLink;

int checks = 0;
void Check(bool condition, string reason) { checks++; if (!condition) throw new Exception(reason); }
var root = Path.Combine(Path.GetTempPath(), "spire-capture-file-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
byte[] ours = [137, 80, 78, 71, 1, 2, 3];
byte[] theirs = [137, 80, 78, 71, 7, 8, 9];
string Output(string name) => Path.Combine(root, name + ".png");
void NoTemporaryFiles() => Check(Directory.GetFiles(root, "*.tmp").Length == 0, "Private temporary files must be cleaned");

try
{
    var existingPath = Output("existing");
    await File.WriteAllBytesAsync(existingPath, theirs);
    PublishedCaptureFile? existingOwner = null;
    bool existingFailed = false;
    try { await CaptureFileWrite.PersistAsync(existingPath, ours, onPersisted: receipt => existingOwner = receipt); }
    catch (IOException) { existingFailed = true; }
    finally { existingOwner?.Delete(); }
    Check(existingFailed && existingOwner == null, "Existing output must reject publication without granting deletion ownership");
    Check(File.ReadAllBytes(existingPath).SequenceEqual(theirs), "A failed capture must never replace or delete existing evidence");
    NoTemporaryFiles();

    // Deterministic TOCTOU: another writer creates the destination after our
    // private file is written but immediately before our atomic publication.
    var racedPath = Output("raced");
    PublishedCaptureFile? racedOwner = null;
    bool racedFailed = false;
    try
    {
        using var write = new CaptureFileWrite(racedPath);
        Check(!File.Exists(racedPath), "TOCTOU destination starts absent");
        await write.Content.WriteAsync(ours);
        await File.WriteAllBytesAsync(racedPath, theirs);
        racedOwner = write.Publish();
    }
    catch (IOException) { racedFailed = true; }
    finally { racedOwner?.Delete(); }
    Check(racedFailed && racedOwner == null, "Losing the publication race must not acquire someone else's path");
    Check(File.ReadAllBytes(racedPath).SequenceEqual(theirs), "A TOCTOU winner's evidence must remain untouched by failure cleanup");
    NoTemporaryFiles();

    var partialPath = Output("partial");
    try
    {
        using var write = new CaptureFileWrite(partialPath);
        await write.Content.WriteAsync(ours.AsMemory(0, 3));
        throw new IOException("Simulated capture writer failure after partial bytes");
    }
    catch (IOException) { }
    Check(!File.Exists(partialPath), "A partially written capture must never publish its output path");
    NoTemporaryFiles();

    using (var cancelled = new CancellationTokenSource())
    {
        var cancelledPath = Output("cancelled");
        PublishedCaptureFile? cancelledOwner = null;
        bool cancellationObserved = false;
        try
        {
            using var write = new CaptureFileWrite(cancelledPath);
            await write.Content.WriteAsync(ours);
            cancelled.Cancel();
            cancelledOwner = write.Publish(cancelled.Token);
        }
        catch (OperationCanceledException) { cancellationObserved = true; }
        Check(cancellationObserved && cancelledOwner == null && !File.Exists(cancelledPath),
            "Cancellation before rename must not publish or grant ownership");
        NoTemporaryFiles();
    }

    // The real persistence entry point must notify only after the file exists.
    var successPath = Output("success");
    PublishedCaptureFile? owner = null;
    bool callbackSawPublishedBytes = false;
    var result = await CaptureFileWrite.PersistAsync(successPath, ours, onPersisted: receipt =>
    {
        owner = receipt;
        callbackSawPublishedBytes = File.ReadAllBytes(receipt.Path).SequenceEqual(ours);
    });
    Check(owner?.Path == successPath && callbackSawPublishedBytes, "Ownership callback runs only after successful publication");
    Check(result.Sha256 == Convert.ToHexString(SHA256.HashData(ours)).ToLowerInvariant(), "Persisted digest must match published bytes");
    owner!.Delete();
    Check(!File.Exists(successPath) && File.Exists(existingPath) && File.Exists(racedPath), "Discard deletes only its successfully published capture");
    NoTemporaryFiles();

    for (int iteration = 0; iteration < 30; iteration++)
    {
        var path = Output("concurrent-" + iteration);
        var winners = new System.Collections.Concurrent.ConcurrentBag<PublishedCaptureFile>();
        var outcomes = await Task.WhenAll(new[] { ours, theirs }.Select(async bytes =>
        {
            try { await CaptureFileWrite.PersistAsync(path, bytes, onPersisted: winners.Add); return true; }
            catch (IOException) { return false; }
        }));
        Check(outcomes.Count(succeeded => succeeded) == 1 && winners.Count == 1,
            "Two concurrent writers to the same output must have exactly one successful owner");
        var retainedBytes = File.ReadAllBytes(path);
        Check(retainedBytes.SequenceEqual(ours) || retainedBytes.SequenceEqual(theirs), "Publication cannot mix or truncate competing bytes");
        winners.Single().Delete();
        NoTemporaryFiles();
    }

    // Reproduce a run change synchronously after rename: the old run's token
    // cancels, but the already-written file must still have a cleanup receipt.
    var runA = JsonSerializer.SerializeToElement(new { context = new { run_id = "run-a" } });
    var runB = JsonSerializer.SerializeToElement(new { context = new { run_id = "run-b" } });
    var budget = new RunCaptureBudget();
    budget.Observe(runA);
    Check(budget.TryReserve(runA, 1, out var oldLease, out _), "Old run obtains a capture reservation");
    PublishedCaptureFile? lateOwner = null;
    var latePath = Output("late-run-a");
    await CaptureFileWrite.PersistAsync(latePath, ours, oldLease!.Token, receipt =>
    {
        lateOwner = receipt;
        budget.Observe(runB);
    });
    Check(oldLease.Token.IsCancellationRequested && !budget.IsCurrent(oldLease) && lateOwner != null,
        "A run change after publication must invalidate capture without losing its ownership receipt");
    lateOwner!.Delete();
    budget.Complete(oldLease, false);
    Check(!File.Exists(latePath) && File.ReadAllBytes(existingPath).SequenceEqual(theirs)
        && File.ReadAllBytes(racedPath).SequenceEqual(theirs), "Late old-run cleanup preserves both kinds of foreign evidence");
    Check(budget.TryReserve(runB, 1, out var freshLease, out _), "Late old-run cleanup cannot consume the new-run quota");
    budget.Complete(freshLease!, true);
    NoTemporaryFiles();
}
finally
{
    // This unique test directory contains only flat test outputs; avoid recursive deletion.
    foreach (var file in Directory.GetFiles(root)) File.Delete(file);
    Directory.Delete(root);
}

Console.WriteLine($"Capture persistence tests passed: {checks} assertions");
