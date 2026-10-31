namespace SpireLink;

// Pure index logic behind the NCardGrid.UpdateGridNavigation guard (no game types, so tests can compile it).
//
// NCardGrid keeps its holders in rows of Columns holders; only the last row of the whole list can be shorter. The grid recycles rows
// while scrolling: ReallocateAbove moves the bottom row to the top. When the window reaches the end of a list whose card count is
// not a multiple of Columns, that bottom row is the short one, and it ends up ABOVE a longer row. UpdateGridNavigation then reads
// _cardRows[i - 1][j] for a j the shorter row does not have (the Bottom neighbour has the matching "j < next row count" check,
// the Top neighbour has none) and throws ArgumentOutOfRangeException. The throw skips the rest of UpdateScrollPosition (the
// scroll container position is never written), so the next frame repeats the reallocation and the throw, every frame.
public static class CardGridNavigation
{
    // True when the game's own loop is safe: every row sits below a row at least as long.
    public static bool IsConsistent(IReadOnlyList<int> rowCounts)
    {
        for (int i = 1; i < rowCounts.Count; i++)
            if (rowCounts[i] > rowCounts[i - 1]) return false;
        return true;
    }

    public readonly record struct Cell(int Row, int Column);
    public readonly record struct Neighbours(Cell Left, Cell Right, Cell Top, Cell Bottom);

    // Same four neighbours as the game's loop for the holder at (i, j) (j < rowCounts[i]); the only difference is that the Top
    // neighbour falls back to the last holder of the row above when that row is shorter than j + 1 (and to itself when it is empty).
    public static Neighbours Of(IReadOnlyList<int> rowCounts, int i, int j)
    {
        int count = rowCounts[i];
        var left = j > 0 ? new Cell(i, j - 1) : new Cell(i, count - 1);
        var right = j < count - 1 ? new Cell(i, j + 1) : new Cell(i, 0);
        Cell top;
        if (i > 0 && rowCounts[i - 1] > 0) top = new Cell(i - 1, Math.Min(j, rowCounts[i - 1] - 1));
        else top = new Cell(i, j);
        var bottom = i < rowCounts.Count - 1 && j < rowCounts[i + 1] ? new Cell(i + 1, j) : new Cell(i, j);
        return new Neighbours(left, right, top, bottom);
    }
}

// One log line per episode: the first inconsistent grid is reported, then nothing until a consistent one is seen again.
public sealed class EpisodeLatch
{
    private bool _active;
    public bool Enter(bool inconsistent)
    {
        if (!inconsistent) { _active = false; return false; }
        if (_active) return false;
        _active = true;
        return true;
    }
}
