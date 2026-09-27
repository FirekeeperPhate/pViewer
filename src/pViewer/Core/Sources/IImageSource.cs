namespace pViewer.Core.Sources;

/// <summary>A browsable page: a file on disk or an archive entry.</summary>
/// <param name="Name">Display name (file name or path inside the archive).</param>
/// <param name="FilePath">Path on disk, null for pages inside an archive.</param>
public sealed record PageInfo(string Name, string? FilePath);

/// <summary>Ordered list of images to browse.</summary>
public interface IImageSource : IDisposable
{
    /// <summary>Folder or archive file the pages come from.</summary>
    string Location { get; }

    bool IsArchive { get; }

    IReadOnlyList<PageInfo> Pages { get; }

    /// <summary>Reads the page bytes into memory (no file stays locked).</summary>
    Task<byte[]> ReadAsync(int index, CancellationToken ct = default);
}
