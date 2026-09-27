namespace pViewer.Core;

public enum PageLayout
{
    /// <summary>Una pagina alla volta.</summary>
    Single,
    /// <summary>Due pagine, lettura da destra a sinistra.</summary>
    Manga,
    /// <summary>Due pagine, lettura da sinistra a destra.</summary>
    Comic,
}

public enum NavigationResult
{
    Moved,
    /// <summary>Si è ricominciato dall'altro capo dell'elenco.</summary>
    Wrapped,
    /// <summary>Superata l'ultima pagina di un archivio: aprire il volume successivo.</summary>
    NextContainer,
    /// <summary>Tornati prima della prima pagina di un archivio: aprire il volume precedente.</summary>
    PreviousContainer,
    NoChange,
}

/// <summary>
/// Posizione nell'elenco delle pagine, con la logica delle coppie manga/comic.
/// In modalità doppia <see cref="Position"/> è la prima pagina (in ordine di lettura) della coppia.
/// </summary>
public sealed class PageNavigator
{
    public int Count { get; private set; }
    public int Position { get; private set; }
    public PageLayout Layout { get; set; } = PageLayout.Single;

    /// <summary>Se vero, oltre i bordi si chiede il volume vicino invece di ricominciare.</summary>
    public bool HasNextContainer { get; set; }
    public bool HasPreviousContainer { get; set; }

    public bool IsDouble => Layout != PageLayout.Single;
    private int Step => IsDouble ? 2 : 1;

    public void Reset(int count, int position = 0)
    {
        Count = Math.Max(0, count);
        Position = Count == 0 ? 0 : Math.Clamp(position, 0, Count - 1);
    }

    /// <summary>Indici delle pagine visibili, nell'ordine in cui vanno disegnate da sinistra a destra.</summary>
    public int[] VisibleIndices()
    {
        if (Count == 0) return [];
        if (!IsDouble || Position + 1 >= Count) return [Position];
        return Layout == PageLayout.Manga ? [Position + 1, Position] : [Position, Position + 1];
    }

    /// <summary>Pagine da precaricare: la vista successiva per prima, poi la precedente.</summary>
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
        int target = Position + Step;
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

    /// <summary>Posizione che mostra l'ultima vista (l'ultima coppia in modalità doppia).</summary>
    public int LastPosition() => Count == 0 ? 0 : IsDouble ? Math.Max(0, Count - 2) : Count - 1;

    /// <summary>Sposta la coppia di una sola pagina (per riallineare le doppie pagine).</summary>
    public bool Shift(int delta)
    {
        int target = Position + delta;
        if (target < 0 || target >= Count) return false;
        Position = target;
        return true;
    }

    public void GoTo(int index)
    {
        if (Count > 0) Position = Math.Clamp(index, 0, Count - 1);
    }
}
