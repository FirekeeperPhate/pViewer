namespace pViewer.Core.Sources;

/// <summary>Tutte le immagini di una cartella (non ricorsivo), in ordine naturale.</summary>
public sealed class FolderSource : IImageSource
{
    private readonly List<PageInfo> _pages;

    private FolderSource(string folder, List<PageInfo> pages)
    {
        Location = folder;
        _pages = pages;
    }

    public string Location { get; }
    public bool IsArchive => false;
    public IReadOnlyList<PageInfo> Pages => _pages;

    public static FolderSource Open(string folder)
    {
        // DirectoryInfo porta gli attributi con l'enumerazione: niente chiamata extra per file.
        var files = new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => ImageFormats.IsImage(f.Name) && (f.Attributes & FileAttributes.Hidden) == 0)
            .Select(f => f.FullName)
            .ToList();
        files.Sort(NaturalComparer.Instance);
        return new FolderSource(folder, files.Select(f => new PageInfo(Path.GetFileName(f), f)).ToList());
    }

    public int IndexOf(string filePath) =>
        _pages.FindIndex(p => string.Equals(p.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

    public Task<byte[]> ReadAsync(int index, CancellationToken ct = default) =>
        File.ReadAllBytesAsync(_pages[index].FilePath!, ct);

    public void Dispose() { }
}
