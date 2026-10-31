using System.Text.Json;

namespace SpireLink;

// Identities come only from ReadGameState's live RunState handle. Never infer
// a new run from a seed, state hash, scene, floor, or combat epoch.
internal sealed class RunCaptureBudget
{
    internal readonly record struct Scope(string? RunId);
    private sealed class Usage { public int Retained; public int Reserved; }
    internal sealed class Lease(Scope scope, long sequence)
    {
        public Scope Scope { get; } = scope;
        public long Sequence { get; } = sequence;
        internal CancellationTokenSource Cancellation { get; } = new();
        public CancellationToken Token => Cancellation.Token;
    }

    private readonly object _sync = new();
    private readonly Dictionary<Scope, Usage> _usage = new();
    private readonly HashSet<Lease> _leases = new();
    private Scope? _current;
    private long _sequence;

    internal static Scope? ReadScope(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object
            || !state.TryGetProperty("context", out var context) || context.ValueKind != JsonValueKind.Object
            || !context.TryGetProperty("run_id", out var run)) return null;
        if (run.ValueKind == JsonValueKind.Null) return new Scope(null);
        return run.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(run.GetString())
            ? new Scope(run.GetString()) : null;
    }

    // Called for every main-thread observation, including unknown transitions.
    // Unknown identity invalidates captures without creating a fresh budget.
    public void Observe(JsonElement state)
    {
        Lease[] stale;
        lock (_sync)
        {
            _current = ReadScope(state);
            stale = _leases.Where(lease => _current != lease.Scope).ToArray();
        }
        foreach (var lease in stale)
            try { lease.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    public bool TryReserve(JsonElement trigger, int limit, out Lease? lease, out string error)
    {
        lock (_sync)
        {
            lease = null;
            var scope = ReadScope(trigger);
            if (scope == null || _current == null) { error = "capture_identity_unknown"; return false; }
            if (scope != _current) { error = "stale_capture_run"; return false; }
            if (!_usage.TryGetValue(scope.Value, out var usage)) _usage[scope.Value] = usage = new();
            if (usage.Retained + usage.Reserved >= limit) { error = "capture_limit"; return false; }
            lease = new(scope.Value, checked(++_sequence));
            usage.Reserved++;
            _leases.Add(lease);
            error = "";
            return true;
        }
    }

    public bool IsCurrent(Lease lease)
    {
        lock (_sync)
            return _leases.Contains(lease) && _current == lease.Scope && !lease.Cancellation.IsCancellationRequested;
    }

    // Completion always settles the original bucket, even after another run
    // starts. Failed writes release only that original reservation.
    public void Complete(Lease lease, bool retained)
    {
        lock (_sync)
        {
            if (!_leases.Remove(lease)) return;
            var usage = _usage[lease.Scope];
            usage.Reserved--;
            if (retained) usage.Retained++;
        }
        lease.Cancellation.Dispose();
    }
}
