namespace pViewer.Core;

public enum PageLayout
{
    /// <summary>One page at a time.</summary>
    Single,
    /// <summary>Two pages, read right to left.</summary>
    Manga,
    /// <summary>Two pages, read left to right.</summary>
    Comic,
}

public enum NavigationResult
{
    Moved,
    /// <summary>Started over from the other end of the list.</summary>
    Wrapped,
    /// <summary>Went past the last page of an archive: open the next volume.</summary>
    NextContainer,
    /// <summary>Went back before the first page of an archive: open the previous volume.</summary>
    PreviousContainer,
    NoChange,
}

/// <summary>
/// Position in the page list, with the manga/comic page pair logic.
/// In two-page mode <see cref="Position"/> is the first page (in reading order) of the pair.
/// </summary>
public sealed class PageNavigator
{
    public int Count { get; private set; }
    public int Position { get; private set; }
    public PageLayout Layout { get; set; } = PageLayout.Single;

    /// <summary>If true, going past the ends asks for the adjacent volume instead of wrapping around.</summary>
    public bool HasNextContainer { get; set; }
    public bool HasPreviousContainer { get; set; }

    public bool IsDouble => Layout != PageLayout.Single;
    private int Step => IsDouble ? 2 : 1;

    /// <summary>
    /// Parity of the first page of each pair: 0 = pairs [0,1] [2,3]…, 1 = pairs [1,2] [3,4]… (after
    /// F12). A position off this parity (only page 0) is shown alone, like a cover.
    /// </summary>
    private int _alignment;

    public void Reset(int count, int position = 0)
    {
        Count = Math.Max(0, count);
        Position = Count == 0 ? 0 : Math.Clamp(position, 0, Count - 1);
        _alignment = Position % 2;
    }

    /// <summary>True when the current page is shown alone although the layout is two-page.</summary>
    private bool IsCover => Position % 2 != _alignment;

    /// <summary>Indices of the visible pages, in the order they are drawn from left to right.</summary>
    public int[] VisibleIndices()
    {
        if (Count == 0) return [];
        if (!IsDouble || IsCover || Position + 1 >= Count) return [Position];
        return Layout == PageLayout.Manga ? [Position + 1, Position] : [Position, Position + 1];
    }

    /// <summary>Pages to preload: the next view first, then the previous one.</summary>
    public int[] PrefetchIndices()
    {
        if (Count == 0) return [];
        var result = new List<int>();
        for (int i = 0; i < Step; i++) result.Add(Position + Step + i);
        for (int i = 0; i < Step; i++) result.Add(Position - Step + i);
        return result.Where(i => i >= 0 && i < Count && !VisibleIndices().Contains(i)).Distinct().ToArray();
    }

    public NavigationResult Next()
    {
        if (Count == 0) return NavigationResult.NoChange;
        int target = Position + (IsCover ? 1 : Step);
        if (target < Count) { Position = target; return NavigationResult.Moved; }
        if (HasNextContainer) return NavigationResult.NextContainer;
        if (Position == 0) return NavigationResult.NoChange;
        Position = 0;
        return NavigationResult.Wrapped;
    }

    public NavigationResult Previous()
    {
        if (Count == 0) return NavigationResult.NoChange;
        if (Position > 0) { Position = Math.Max(0, Position - Step); return NavigationResult.Moved; }
        if (HasPreviousContainer) return NavigationResult.PreviousContainer;
        int last = LastPosition();
        if (last == Position) return NavigationResult.NoChange;
        Position = last;
        return NavigationResult.Wrapped;
    }

    public void First() => Position = 0;

    public void Last() => Position = LastPosition();

    /// <summary>Position that shows the last view (the last pair in two-page mode).</summary>
    public int LastPosition()
    {
        if (Count == 0) return 0;
        if (!IsDouble) return Count - 1;
        // Keep the pairing used going forward:
        // with 5 pages and pairs [0,1] [2,3], the last view is [4] alone, not [3,4].
        int last = Count - 1;
        if (last % 2 != _alignment) last--;
        return Math.Max(0, last);
    }

    /// <summary>Shifts the pair by a single page (to realign double-page spreads).</summary>
    public bool Shift(int delta)
    {
        int target = Position + delta;
        if (target < 0 || target >= Count) return false;
        Position = target;
        _alignment = target % 2;
        return true;
    }

    public void GoTo(int index)
    {
        if (Count > 0) Position = Math.Clamp(index, 0, Count - 1);
    }
}
