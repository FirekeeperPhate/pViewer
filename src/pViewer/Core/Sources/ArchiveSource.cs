using SharpCompress.Archives;
using SharpCompress.Readers;

namespace pViewer.Core.Sources;

/// <summary>
/// Immagini contenute in un archivio (zip/cbz, rar/cbr, 7z/cb7), lette direttamente in memoria:
/// niente cartella temporanea su disco. Gli archivi annidati vengono "appiattiti".
/// Per gli archivi solidi l'accesso casuale costerebbe una decompressione dall'inizio del blocco
/// a ogni pagina, quindi vengono letti una volta sola in sequenza, in background.
/// </summary>
public sealed class ArchiveSource : IImageSource
{
    private const int MaxNestingDepth = 3;

    private sealed class Slot(IArchive archive, IArchiveEntry entry, string name)
    {
        public IArchive Archive { get; } = archive;
        public IArchiveEntry Entry { get; } = entry;
        public string Name { get; } = name;
        public TaskCompletionSource<byte[]>? Preload { get; set; }
    }

    private readonly List<Slot> _slots;
    private readonly List<PageInfo> _pages;
    private readonly List<IDisposable> _owned;
    private readonly CancellationTokenSource _cts = new();
    private Task? _preloadTask;
    private bool _disposed;

    private ArchiveSource(string path, List<Slot> slots, List<IDisposable> owned)
    {
        Location = path;
        _slots = slots;
        _owned = owned;
        _pages = slots.Select(s => new PageInfo(s.Name, null)).ToList();
    }

    public string Location { get; }
    public bool IsArchive => true;
    public IReadOnlyList<PageInfo> Pages => _pages;

    public static ArchiveSource Open(string path)
    {
        var owned = new List<IDisposable>();
        var slots = new List<Slot>();
        IArchive root;
        try
        {
            root = ArchiveFactory.OpenArchive(path, new ReaderOptions());
            owned.Add(root);
            if (SafeIsEncrypted(root))
                throw new InvalidDataException("The archive is password protected: not supported.");
            Collect(root, "", slots, owned, 0);
        }
        catch
        {
            foreach (var d in owned) d.Dispose();
            throw;
        }

        if (slots.Count == 0)
        {
            foreach (var d in owned) d.Dispose();
            throw new InvalidDataException("The archive contains no images.");
        }

        slots.Sort((a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
        var source = new ArchiveSource(path, slots, owned);
        if (SafeIsSolid(root)) source.StartSequentialPreload(root);
        return source;
    }

    private static bool SafeIsEncrypted(IArchive a) { try { return a.IsEncrypted; } catch { return false; } }
    private static bool SafeIsSolid(IArchive a) { try { return a.IsSolid; } catch { return false; } }

    private static bool IsJunk(string key)
    {
        // Metadati di macOS che finiscono spesso negli zip dei fumetti.
        string norm = key.Replace('\\', '/');
        return norm.Contains("__MACOSX/", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(norm).StartsWith("._", StringComparison.Ordinal);
    }

    private static void Collect(IArchive archive, string prefix, List<Slot> slots, List<IDisposable> owned, int depth)
    {
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key) || IsJunk(entry.Key)) continue;

            if (ImageFormats.IsImage(entry.Key))
            {
                slots.Add(new Slot(archive, entry, prefix + entry.Key));
            }
            else if (depth < MaxNestingDepth && ImageFormats.IsArchive(entry.Key))
            {
                var ms = new MemoryStream();
                try
                {
                    using (var s = entry.OpenEntryStream()) s.CopyTo(ms);
                    ms.Position = 0;
                    var inner = ArchiveFactory.OpenArchive(ms, new ReaderOptions());
                    owned.Add(inner);
                    owned.Add(ms);
                    Collect(inner, prefix + entry.Key + "/", slots, owned, depth + 1);
                }
                catch (Exception) when (!owned.Contains(ms))
                {
                    ms.Dispose(); // archivio interno illeggibile: lo saltiamo
                }
            }
        }
    }

    private void StartSequentialPreload(IArchive root)
    {
        var byKey = new Dictionary<string, Slot>(StringComparer.Ordinal);
        foreach (var slot in _slots.Where(s => s.Archive == root))
        {
            slot.Preload = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            byKey.TryAdd(slot.Entry.Key!, slot);
        }

        var token = _cts.Token;
        _preloadTask = Task.Run(() =>
        {
            try
            {
                lock (root)
                {
                    using var reader = root.ExtractAllEntries();
                    while (reader.MoveToNextEntry())
                    {
                        token.ThrowIfCancellationRequested();
                        var key = reader.Entry.Key;
                        if (key is null || !byKey.TryGetValue(key, out var slot)) continue;
                        using var ms = new MemoryStream();
                        reader.WriteEntryTo(ms);
                        slot.Preload!.TrySetResult(ms.ToArray());
                    }
                }
                foreach (var slot in byKey.Values)
                    slot.Preload!.TrySetException(new FileNotFoundException("Entry not found in the archive.", slot.Name));
            }
            catch (Exception ex)
            {
                foreach (var slot in byKey.Values)
                {
                    if (ex is OperationCanceledException) slot.Preload!.TrySetCanceled();
                    else slot.Preload!.TrySetException(ex);
                }
            }
        });
    }

    public Task<byte[]> ReadAsync(int index, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var slot = _slots[index];
        if (slot.Preload is not null) return slot.Preload.Task.WaitAsync(ct);

        return Task.Run(() =>
        {
            // Gli archivi di SharpCompress non sono thread-safe.
            lock (slot.Archive)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                using var s = slot.Entry.OpenEntryStream();
                using var ms = new MemoryStream(slot.Entry.Size > 0 ? (int)Math.Min(slot.Entry.Size, int.MaxValue) : 0);
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }, ct);
    }

    /// <summary>Archivi nella stessa cartella, in ordine naturale (per passare al volume successivo).</summary>
    public static List<string> Siblings(string archivePath)
    {
        string? dir = Path.GetDirectoryName(archivePath);
        if (dir is null || !Directory.Exists(dir)) return [archivePath];
        var list = Directory.EnumerateFiles(dir).Where(ImageFormats.IsArchive).ToList();
        list.Sort(NaturalComparer.Instance);
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();

        void DisposeOwned()
        {
            foreach (var d in _owned)
            {
                if (d is IArchive a) { lock (a) a.Dispose(); }
                else d.Dispose();
            }
            _cts.Dispose();
        }

        if (_preloadTask is { IsCompleted: false }) _preloadTask.ContinueWith(_ => DisposeOwned(), TaskScheduler.Default);
        else DisposeOwned();
    }
}
