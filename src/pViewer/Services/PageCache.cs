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

    public Entry Get(int index)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(index, out var existing)) return existing;
            // Each page has its own cancellation, so pages passed while scrolling fast are dropped
            // (see Trim) instead of all being read and decoded in parallel.
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var token = cancellation.Token;
            var bytes = _source.ReadAsync(index, token);
            string name = _source.Pages[index].Name;
            var image = bytes.ContinueWith(
                t => ImageDecoder.Decode(t.GetAwaiter().GetResult(), name, _autoOrient),
                token, TaskContinuationOptions.RunContinuationsAsynchronously, TaskScheduler.Default);
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
        _cts.Cancel();
        lock (_lock)
        {
            foreach (int k in _entries.Keys.ToList()) Drop(k);
        }
        _cts.Dispose();
    }
}
