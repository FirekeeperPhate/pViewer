using pViewer.Core.Sources;
using pViewer.Imaging;

namespace pViewer.Services;

/// <summary>
/// Loads the pages of a source in the background and keeps the ones near the
/// current position in memory, so next/previous are instant.
/// </summary>
public sealed class PageCache : IDisposable
{
    public sealed class Entry
    {
        public required Task<byte[]> Bytes { get; init; }
        public required Task<LoadedImage> Image { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
    }

    private readonly IImageSource _source;
    private readonly bool _autoOrient;
    private readonly Dictionary<int, Entry> _entries = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();

    public PageCache(IImageSource source, bool autoOrient)
    {
        _source = source;
        _autoOrient = autoOrient;
    }

    public IImageSource Source => _source;

    /// <summary>
    /// Full decodes running at the same time. Pages passed while scrolling fast wait here and are
    /// cancelled (see Trim) before they start, instead of all decoding in parallel.
    /// </summary>
    private static readonly SemaphoreSlim DecodeGate = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));

    private bool _disposed;

    public Entry Get(int index)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                // A late continuation (after the source changed or the window closed).
                var cancelled = Task.FromCanceled<byte[]>(new CancellationToken(true));
                return new Entry { Bytes = cancelled, Image = Task.FromCanceled<LoadedImage>(new CancellationToken(true)),
                                   Cancellation = new CancellationTokenSource() };
            }
            if (_entries.TryGetValue(index, out var existing)) return existing;
            // Each page has its own cancellation, so pages passed while scrolling fast are dropped.
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var token = cancellation.Token;
            // The file opened from Explorer, already read and decoded while the window was being built.
            if (StartupPreload.TryTake(_source.Pages[index].FilePath, _autoOrient, out var preBytes, out var preImage))
            {
                var preloaded = new Entry { Bytes = preBytes, Image = preImage, Cancellation = cancellation };
                _entries[index] = preloaded;
                return preloaded;
            }
            Task<byte[]> bytes;
            try { bytes = _source.ReadAsync(index, token); }
            catch (Exception ex) { bytes = Task.FromException<byte[]>(ex); } // a source that throws synchronously
            string name = _source.Pages[index].Name;
            var image = Task.Run(async () =>
            {
                var data = await bytes.ConfigureAwait(false);
                await DecodeGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    return ImageDecoder.Decode(data, name, _autoOrient);
                }
                finally
                {
                    DecodeGate.Release();
                }
            }, token);
            // Exceptions of preloads that were never requested must not stay "unobserved".
            _ = bytes.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            _ = image.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var entry = new Entry { Bytes = bytes, Image = image, Cancellation = cancellation };
            _entries[index] = entry;
            return entry;
        }
    }

    private void Drop(int index, bool cancel = true)
    {
        if (!_entries.Remove(index, out var entry)) return;
        if (!cancel) return;
        entry.Cancellation.Cancel();
        entry.Cancellation.Dispose();
    }

    public bool TryGetLoaded(int index, out LoadedImage? image)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(index, out var e) && e.Image.IsCompletedSuccessfully)
            {
                image = e.Image.Result;
                return true;
            }
        }
        image = null;
        return false;
    }

    public void Prefetch(IEnumerable<int> indices)
    {
        foreach (int i in indices)
            if (i >= 0 && i < _source.Pages.Count) Get(i);
    }

    /// <summary>Releases everything except the given pages.</summary>
    public void Trim(IEnumerable<int> keep)
    {
        var set = keep.ToHashSet();
        lock (_lock)
        {
            foreach (int k in _entries.Keys.Where(k => !set.Contains(k)).ToList())
                Drop(k);
        }
    }

    /// <summary>
    /// Forgets a page so it is read again next time. Not cancelled: the current request may be
    /// waiting on it (e.g. F5 while a save of the same file finishes).
    /// </summary>
    public void Invalidate(int index)
    {
        lock (_lock) Drop(index, cancel: false);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            foreach (int k in _entries.Keys.ToList()) Drop(k);
        }
        _cts.Dispose();
    }
}
