namespace pViewer.Core.Sources;

/// <summary>All the images in a folder (not recursive), in natural order.</summary>
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

    /// <param name="include">A file opened explicitly (double-click on a hidden image): listed even
    /// if hidden, so it is the one shown.</param>
    public static FolderSource Open(string folder, string? include = null)
    {
        // DirectoryInfo brings the attributes with the enumeration: no extra call per file.
        var files = new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => ImageFormats.IsImage(f.Name)
                        && ((f.Attributes & FileAttributes.Hidden) == 0
                            || string.Equals(f.FullName, include, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.FullName)
            .ToList();
        files.Sort(NaturalComparer.Instance);
        return new FolderSource(folder, files.Select(f => new PageInfo(Path.GetFileName(f), f)).ToList());
    }

    public int IndexOf(string filePath) =>
        _pages.FindIndex(p => string.Equals(p.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

    // async: a missing or locked file (deleted in Explorer, still being copied) must fail the
    // returned task, not throw synchronously into the caller.
    public async Task<byte[]> ReadAsync(int index, CancellationToken ct = default) =>
        await File.ReadAllBytesAsync(_pages[index].FilePath!, ct);

    public void Dispose() { }
}
