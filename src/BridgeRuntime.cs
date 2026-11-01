using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SpireLink;

internal static class KnowledgeText
{
    private static readonly Regex EnergyIcon = new(@"\[[a-z0-9_]*energy_icon\.png\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    public static string NormalizeEnergyIcons(string text) => EnergyIcon.Replace(text, "[energy]");
}

/// <summary>Compares the running game version with the versions a release was verified on.</summary>
public static class GameVersionCheck
{
    // The game reports versions like "v0.107.1"; a missing version reads as unknown, never as tested.
    public static string Compatibility(string? gameVersion, IEnumerable<string> tested)
    {
        if (string.IsNullOrWhiteSpace(gameVersion)) return "unknown_game_version";
        string normalized = gameVersion.Trim().TrimStart('v', 'V');
        return tested.Any(t => string.Equals(t.TrimStart('v', 'V'), normalized, StringComparison.OrdinalIgnoreCase))
            ? "tested" : "untested_game_version";
    }
}

/// <summary>Cancellation removes only queued work; a running game mutation is never interrupted.</summary>
public sealed class QueuedInvocation<T>
{
    private const int Queued = 0, Running = 1, Finished = 2, Cancelled = 3;
    private readonly Func<T> _work;
    private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationToken _token;
    private CancellationTokenRegistration _registration;
    private int _stage;
    public Task<T> Task => _completion.Task;
    public bool Started => Volatile.Read(ref _stage) is Running or Finished;

    public QueuedInvocation(Func<T> work, CancellationToken token)
    {
        _work = work;
        _token = token;
        _registration = token.Register(() =>
        {
            if (Interlocked.CompareExchange(ref _stage, Cancelled, Queued) == Queued)
                _completion.TrySetCanceled(_token);
        });
    }

    public void Run()
    {
        if (Interlocked.CompareExchange(ref _stage, Running, Queued) != Queued)
        {
            _registration.Dispose();
            return;
        }
        try { _completion.TrySetResult(_work()); }
        catch (Exception ex) { _completion.TrySetException(ex); }
        finally
        {
            Volatile.Write(ref _stage, Finished);
            _registration.Dispose();
        }
    }
}


public enum ReceiptAdd { Added, Exists, Full }

/// <summary>
/// Receipts by request id. When the store is full the oldest RESOLVED receipts are evicted (down to 90 % of the capacity); the predicate decides what resolved
/// means (the mod: the request finished with a final status, or its late settlement was observed). A receipt that is still running or still unresolved is
/// never evicted, so "Full" means that nothing old enough could go. Re-sending the id of a receipt that is still stored answers with that same receipt (idempotency);
/// an id that was evicted is unknown again, which is harmless because a request id older than the whole window cannot match the current state version anyway.
/// Callers that need get-then-add as one step lock <see cref="Sync"/> (the lock is re-entrant).
/// </summary>
public sealed class ReceiptStore<T> where T : class
{
    private readonly Dictionary<string, T> _items = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Func<T, bool> _finished;
    private readonly object _gate = new();
    public int Capacity { get; }
    public long Evicted { get; private set; }

    public ReceiptStore(int capacity, Func<T, bool> isFinished)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _finished = isFinished;
    }

    public object Sync => _gate;
    public int Count { get { lock (_gate) return _items.Count; } }
    public bool TryGet(string id, out T item) { lock (_gate) return _items.TryGetValue(id, out item!); }

    public ReceiptAdd TryAdd(string id, T item)
    {
        lock (_gate)
        {
            if (_items.ContainsKey(id)) return ReceiptAdd.Exists;
            if (_items.Count >= Capacity) EvictFinished();
            if (_items.Count >= Capacity) return ReceiptAdd.Full;
            _items[id] = item;
            _order.Enqueue(id);
            return ReceiptAdd.Added;
        }
    }

    private void EvictFinished()
    {
        int target = Math.Max(1, Capacity / 10 * 9);
        int attempts = _order.Count;
        while (attempts-- > 0 && _items.Count > target && _order.Count > 0)
        {
            string id = _order.Dequeue();
            if (!_items.TryGetValue(id, out var item)) continue;
            if (_finished(item)) { _items.Remove(id); Evicted++; }
            else _order.Enqueue(id);
        }
    }
}

/// <summary>
/// How a receipt's <c>status</c> maps to the HTTP layer and to the receipt store's retention. A receipt is unresolved while
/// the game has not yet shown the action's effect (accepted, pending, unknown); the store never evicts an unresolved receipt,
/// and both /v1/actions and /v1/requests answer it with 202.
/// </summary>
public static class ReceiptStatus
{
    public static bool IsUnresolved(string? status) => status is "accepted" or "pending" or "unknown";

    public static int HttpStatus(string? status) => status switch
    {
        "rejected" => 409,
        "accepted" or "pending" => 202,
        _ => 200
    };

    public static string? Of(JsonElement receipt)
        => receipt.ValueKind == JsonValueKind.Object && receipt.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}

/// <summary>Where the evidence journal may not live: inside the game's mods folder, inside the mod's own folder (a Workshop
/// update replaces it) or anywhere under a Steam Workshop content folder, because the game reads every .json there as a mod manifest.</summary>
public static class DataDirectoryPolicy
{
    public static string? Reject(string candidate, string? gameDirectory, string? modDirectory)
    {
        string full;
        try { full = Path.GetFullPath(candidate); }
        catch (Exception ex) { return "data_directory is not a valid path: " + ex.Message; }
        // The written text and where it really leads (through junctions and symbolic links) are both checked, against the
        // restricted folders as written and as they really are.
        var paths = new[] { full, ResolveLinks(full) }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!string.IsNullOrEmpty(gameDirectory) && UnderAny(paths, Path.Combine(gameDirectory, "mods")))
            return "data_directory is inside the game's mods folder; the game would read the journal's .json files as mod manifests";
        if (!string.IsNullOrEmpty(modDirectory) && UnderAny(paths, modDirectory))
            return "data_directory is inside the mod's own folder; a Workshop update replaces that folder";
        foreach (var path in paths)
        {
            var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (int i = 0; i + 2 < parts.Length; i++)
                if (parts[i].Equals("steamapps", StringComparison.OrdinalIgnoreCase) && parts[i + 1].Equals("workshop", StringComparison.OrdinalIgnoreCase)
                    && parts[i + 2].Equals("content", StringComparison.OrdinalIgnoreCase))
                    return "data_directory is inside a Steam Workshop content folder";
        }
        return null;
    }

    /// <summary>
    /// The full path with every existing directory on it that is a junction or a symbolic link replaced by its final target,
    /// root first; the part that does not exist yet is appended unchanged. A path whose links cannot be read comes back as given.
    /// </summary>
    public static string ResolveLinks(string fullPath)
    {
        try
        {
            string full = Path.GetFullPath(fullPath);
            // Split off the trailing components that do not exist yet; they cannot be links.
            var missing = new Stack<string>();
            string? existing = full;
            while (existing != null && !Directory.Exists(existing))
            {
                var name = Path.GetFileName(existing);
                if (name.Length == 0) return full;
                missing.Push(name);
                existing = Path.GetDirectoryName(existing);
            }
            if (existing == null) return full;
            string? root = Path.GetPathRoot(existing);
            if (string.IsNullOrEmpty(root)) return full;
            string resolved = root;
            foreach (var part in existing[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                resolved = Path.Combine(resolved, part);
                FileSystemInfo? target = null;
                try { target = new DirectoryInfo(resolved).ResolveLinkTarget(returnFinalTarget: true); }
                catch (Exception) { /* a cycle or an unreadable link: keep the path as written from here on */ }
                if (target != null) resolved = Path.GetFullPath(target.FullName);
            }
            while (missing.Count > 0) resolved = Path.Combine(resolved, missing.Pop());
            return Path.GetFullPath(resolved);
        }
        catch (Exception)
        {
            return fullPath;
        }
    }

    private static bool UnderAny(string[] paths, string root)
    {
        string normalizedRoot;
        try { normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return false; }
        var roots = new[] { normalizedRoot, ResolveLinks(normalizedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) };
        return paths.Any(path => roots.Any(r => Under(path, r)));
    }

    private static bool Under(string full, string normalizedRoot)
    {
        if (full.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return true;
        return full.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Late settlement augments an immutable dispatch receipt without losing its original outcome.</summary>
public sealed class ReceiptResolution
{
    private readonly object _gate = new();
    private long? _version;
    private string? _hash;
    private DateTimeOffset _at;

    public bool Settled { get { lock (_gate) return _version.HasValue; } }

    public void MarkSettled(long version, string hash, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_version.HasValue) return;
            _version = version;
            _hash = hash;
            _at = at;
        }
    }

    public object Apply(object response, JsonSerializerOptions options)
    {
        lock (_gate)
        {
            if (!_version.HasValue) return response;
            var root = JsonSerializer.SerializeToNode(response, options) as JsonObject;
            if (root == null) return response;
            string? original = root["status"]?.GetValue<string>();
            if (original is not ("accepted" or "pending" or "unknown")) return response;
            root["initial_status"] = original;
            root["status"] = "completed";
            root["settled"] = true;
            root["settled_at"] = JsonValue.Create(_at);
            root["completion_basis"] = "changed_state_ready_and_action_postcondition_observed";
            root["observation_ref"] = new JsonObject { ["state_version"] = _version.Value, ["state_hash"] = _hash };
            root.Remove("observation");
            if (root.Remove("error", out var error)) root["initial_error"] = error;
            root["message"] = "Interaction readiness was later observed; inspect the current state for game effects.";
            return JsonSerializer.SerializeToElement(root, options);
        }
    }
}

public static class DecisionMetadata
{
    public static string? Validate(JsonElement decision)
    {
        if (decision.ValueKind != JsonValueKind.Object) return "decision must be an object";
        if (!decision.TryGetProperty("importance", out var importance)) return null;
        if (importance.ValueKind != JsonValueKind.Object) return "importance must be an object";
        if (!importance.TryGetProperty("level", out var level) || level.ValueKind != JsonValueKind.String
            || level.GetString() is not ("routine" or "notable" or "major"))
            return "importance.level must be routine, notable, or major";
        if (!importance.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(reason.GetString()) || reason.GetString()!.Length > 500)
            return "importance.reason must contain 1..500 characters";
        if (importance.TryGetProperty("capture", out var capture) && capture.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return "importance.capture must be a boolean";
        if (importance.TryGetProperty("tags", out var tags)
            && (tags.ValueKind != JsonValueKind.Array || tags.GetArrayLength() > 10
                || tags.EnumerateArray().Any(tag => tag.ValueKind != JsonValueKind.String || tag.GetString()!.Length > 64)))
            return "importance.tags must contain at most 10 strings of at most 64 characters";
        return null;
    }
}
