using SpireLink;

static class Tests
{
    private static int _count;
    private static void Check(bool condition, string message)
    {
        _count++;
        if (!condition) throw new Exception(message);
    }

    // The game's UpdateGridNavigation index arithmetic, verbatim, over row sizes only. Throws like List<T> does.
    private static CardGridNavigation.Neighbours Original(int[] rows, int i, int j)
    {
        int At(int r, int c) { if (r < 0 || r >= rows.Length || c < 0 || c >= rows[r]) throw new ArgumentOutOfRangeException(); return c; }
        var left = j > 0 ? new CardGridNavigation.Cell(i, At(i, j - 1)) : new CardGridNavigation.Cell(i, At(i, rows[i] - 1));
        var right = j < rows[i] - 1 ? new CardGridNavigation.Cell(i, At(i, j + 1)) : new CardGridNavigation.Cell(i, At(i, 0));
        var top = i > 0 ? new CardGridNavigation.Cell(i - 1, At(i - 1, j)) : new CardGridNavigation.Cell(i, At(i, j));
        var bottom = i < rows.Length - 1 && j < rows[i + 1] ? new CardGridNavigation.Cell(i + 1, At(i + 1, j)) : new CardGridNavigation.Cell(i, At(i, j));
        return new CardGridNavigation.Neighbours(left, right, top, bottom);
    }

    // Row sizes of a grid with `cards` cards, `columns` columns, rotated like ReallocateAbove (bottom row to the top) `rotations` times.
    private static int[] Rows(int cards, int columns, int rotations)
    {
        var rows = new List<int>();
        for (int left = cards; left > 0; left -= columns) rows.Add(Math.Min(columns, left));
        for (int r = 0; r < rotations; r++) { var last = rows[^1]; rows.RemoveAt(rows.Count - 1); rows.Insert(0, last); }
        return rows.ToArray();
    }

    public static void Main()
    {
        // Consistent grids: the guard hands over to the game's loop, and its own answer is identical anyway.
        int consistentGrids = 0;
        for (int columns = 1; columns <= 8; columns++)
            for (int cards = 1; cards <= 40; cards++)
            {
                var rows = Rows(cards, columns, 0);
                Check(CardGridNavigation.IsConsistent(rows), $"unrotated grid {cards}/{columns} must be consistent");
                consistentGrids++;
                for (int i = 0; i < rows.Length; i++)
                    for (int j = 0; j < rows[i]; j++)
                        Check(CardGridNavigation.Of(rows, i, j) == Original(rows, i, j), $"neighbours differ on consistent grid {cards}/{columns} at {i},{j}");
            }

        // The bug: a short bottom row rotated above a full one. The original throws, the guard does not, and every neighbour is a real holder.
        int throwing = 0;
        for (int columns = 2; columns <= 8; columns++)
            for (int cards = columns + 1; cards <= 40; cards++)
            {
                if (cards % columns == 0) continue;
                var rows = Rows(cards, columns, 1);
                Check(!CardGridNavigation.IsConsistent(rows), $"rotated grid {cards}/{columns} must be flagged");
                bool threw = false;
                for (int i = 0; i < rows.Length; i++)
                    for (int j = 0; j < rows[i]; j++)
                    {
                        try { Original(rows, i, j); } catch (ArgumentOutOfRangeException) { threw = true; }
                        var n = CardGridNavigation.Of(rows, i, j);
                        foreach (var c in new[] { n.Left, n.Right, n.Top, n.Bottom })
                            Check(c.Row >= 0 && c.Row < rows.Length && c.Column >= 0 && c.Column < rows[c.Row], $"neighbour out of range {cards}/{columns} at {i},{j}");
                    }
                Check(threw, $"original arithmetic must throw on flagged grid {cards}/{columns}");
                throwing++;
            }

        // Every shape the original throws on is flagged (no throw escapes the consistency check), and every unflagged shape is a no-throw.
        var rng = new Random(7);
        for (int n = 0; n < 5000; n++)
        {
            var rows = Enumerable.Range(0, rng.Next(1, 7)).Select(_ => rng.Next(0, 6)).ToArray();
            bool flagged = !CardGridNavigation.IsConsistent(rows);
            bool threw = false;
            for (int i = 0; i < rows.Length && !threw; i++)
                for (int j = 0; j < rows[i]; j++)
                {
                    try { Original(rows, i, j); } catch (ArgumentOutOfRangeException) { threw = true; break; }
                }
            if (threw) Check(flagged, "a throwing shape must be flagged: " + string.Join(",", rows));
            if (!flagged) Check(!threw, "an unflagged shape must not throw: " + string.Join(",", rows));
            for (int i = 0; i < rows.Length; i++)
                for (int j = 0; j < rows[i]; j++)
                {
                    var nb = CardGridNavigation.Of(rows, i, j);
                    foreach (var c in new[] { nb.Left, nb.Right, nb.Top, nb.Bottom })
                        Check(c.Row >= 0 && c.Row < rows.Length && c.Column >= 0 && c.Column < rows[c.Row], "guard neighbour out of range: " + string.Join(",", rows));
                }
        }

        // Episode latch logs once per episode.
        var latch = new EpisodeLatch();
        Check(!latch.Enter(false), "consistent frame is not an episode");
        Check(latch.Enter(true), "first inconsistent frame opens an episode");
        Check(!latch.Enter(true) && !latch.Enter(true), "later inconsistent frames are silent");
        Check(!latch.Enter(false), "consistent frame closes the episode");
        Check(latch.Enter(true), "a new episode logs again");

        Console.WriteLine($"CardGridTests passed: {_count} checks, {consistentGrids} consistent grids, {throwing} throwing grids");
    }
}
