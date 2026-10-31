using SpireLink;

int checks = 0;
void Check(bool condition, string why) { checks++; if (!condition) throw new Exception(why); }

// the entry type: a request that is finished or still running
var running = new List<Entry>();
ReceiptStore<Entry> Store(int capacity) => new(capacity, e => e.Done);

{   // below the capacity nothing is evicted
    var s = Store(10);
    for (int i = 0; i < 9; i++) Check(s.TryAdd("r" + i, new Entry(true)) == ReceiptAdd.Added, "add below capacity");
    Check(s.Count == 9 && s.Evicted == 0, "no eviction below capacity");
}
{   // full of finished receipts: the oldest go first, down to 90 %, the newest stay
    var s = Store(10);
    for (int i = 0; i < 10; i++) s.TryAdd("r" + i, new Entry(true));
    Check(s.TryAdd("new", new Entry(true)) == ReceiptAdd.Added, "a full store of finished receipts accepts a new request");
    Check(s.Count == 10 && s.Evicted == 1, $"evicts down to 90 % then adds (count {s.Count}, evicted {s.Evicted})");
    Check(!s.TryGet("r0", out _) && s.TryGet("r1", out _) && s.TryGet("r9", out _) && s.TryGet("new", out _), "the oldest receipts were evicted, the newer ones kept");
}
{   // same id re-sent: the same receipt object, no second entry
    var s = Store(10);
    var first = new Entry(true);
    s.TryAdd("same", first);
    Check(s.TryAdd("same", new Entry(true)) == ReceiptAdd.Exists, "a stored id is reported as existing");
    Check(s.TryGet("same", out var got) && ReferenceEquals(got, first) && s.Count == 1, "idempotent replay returns the original receipt");
    for (int i = 0; i < 8; i++) s.TryAdd("x" + i, new Entry(true));
    Check(s.TryGet("same", out got) && ReferenceEquals(got, first), "a receipt inside the window survives other traffic");
}
{   // running requests are never evicted; a store full of them refuses (and says so), then recovers when they finish
    var s = Store(10);
    var live = Enumerable.Range(0, 10).Select(_ => new Entry(false)).ToList();
    for (int i = 0; i < 10; i++) s.TryAdd("p" + i, live[i]);
    Check(s.TryAdd("late", new Entry(true)) == ReceiptAdd.Full, "full of unfinished requests: refused");
    Check(s.Count == 10 && s.Evicted == 0 && s.TryGet("p0", out _), "nothing unfinished was evicted");
    live[0].Done = true; live[1].Done = true;
    Check(s.TryAdd("late", new Entry(true)) == ReceiptAdd.Added, "after some finish, the store accepts again");
    Check(s.TryGet("p9", out _), "unfinished receipts are kept while finished ones go");
}
{   // a long run never hits the capacity
    var s = Store(1024);
    for (int i = 0; i < 100000; i++) Check(s.TryAdd("q" + i, new Entry(true)) == ReceiptAdd.Added, "endless stream of finished requests");
    Check(s.Count <= 1024 && s.Evicted > 0, "bounded memory");
}
Console.WriteLine($"ReceiptStore tests passed ({checks} checks)");

class Entry(bool done) { public bool Done = done; }
