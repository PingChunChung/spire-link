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
/// Receipts by request id. When the store is full the oldest FINISHED receipts are evicted (down to 90 % of the capacity); a receipt whose request is still running is
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

/// <summary>Late settlement augments an immutable dispatch receipt without losing its original outcome.</summary>
public sealed class ReceiptResolution
{
    private readonly object _gate = new();
    private long? _version;
    private string? _hash;
    private DateTimeOffset _at;

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
