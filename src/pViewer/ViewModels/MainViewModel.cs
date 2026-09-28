using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using pViewer.Controls;
using pViewer.Core;
using pViewer.Core.Sources;
using pViewer.Imaging;
using pViewer.Services;

namespace pViewer.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const long PreviewThresholdBytes = 1_000_000;

    private readonly IMainView _view;
    private readonly PageNavigator _nav = new();
    private readonly DispatcherTimer _slideshowTimer = new();
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

    private IImageSource? _source;
    private PageCache? _cache;
    private List<string> _archiveSiblings = [];
    private int _archiveIndex = -1;
    /// <summary>Archives that failed to open (skipped when moving between volumes).</summary>
    private readonly HashSet<string> _brokenArchives = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loaded pages currently visible (null if the image is "detached", e.g. from the clipboard).</summary>
    private LoadedImage[]? _visible;
    private EditSession? _edit;
    /// <summary>Image not tied to a file: pasted, or two pages joined to edit them.</summary>
    private bool _detached;
    private string _detachedName = "";
    private int _requestId;
    /// <summary>An archive (e.g. the next volume) is being opened in the background.</summary>
    private int _archiveOpenRequest = -1;
    /// <summary>Edits run one at a time, in the order they were requested (none is dropped).</summary>
    private readonly SemaphoreSlim _editGate = new(1, 1);

    /// <summary>
    /// Navigation waits for running saves/edits (a save must finish on the page it started on)
    /// and for a volume being opened (it would be opened again).
    /// </summary>
    private bool CanNavigate => _source is not null && !IsBusy && !IsOpeningArchive;

    /// <summary>True while the latest request is an archive that is still opening.</summary>
    private bool IsOpeningArchive => _archiveOpenRequest == _requestId;

    public MainViewModel(IMainView view, AppSettings settings)
    {
        _view = view;
        Settings = settings;
        _viewMode = settings.ViewMode;
        _slideshowTimer.Tick += (_, _) => SlideshowTick();
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastText = null; };
    }

    public AppSettings Settings { get; }

    // ---- Observable state ----

    [ObservableProperty] private string _title = "pViewer";
    [ObservableProperty] private string _statusName = "";
    [ObservableProperty] private string _statusInfo = "";
    [ObservableProperty] private string _zoomText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private string? _toastText;
    [ObservableProperty] private bool _hasImage;
    [ObservableProperty] private ViewMode _viewMode;
    [ObservableProperty] private PageLayout _layout = PageLayout.Single;
    [ObservableProperty] private bool _whiteBackground;
    [ObservableProperty] private bool _isSlideshowRunning;
    [ObservableProperty] private ViewerTool _tool;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    [ObservableProperty] private bool _isModified;

    public bool ShowWelcome => !HasImage && !IsLoading && ErrorText is null;
    partial void OnHasImageChanged(bool value) => OnPropertyChanged(nameof(ShowWelcome));
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowWelcome));
    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(ShowWelcome));

    partial void OnViewModeChanged(ViewMode value)
    {
        if (value != ViewMode.ActualSize) Settings.ViewMode = value;
    }

    private bool IsSingleFilePage =>
        !_detached && _source is FolderSource && _nav.VisibleIndices().Length == 1 && CurrentFilePath is not null;

    /// <summary>File on disk of the current page (null for archives and detached images).</summary>
    public string? CurrentFilePath =>
        _detached || _source is null || _nav.Count == 0 ? null : _source.Pages[_nav.Position].FilePath;

    /// <summary>The "physical" file behind the view: the page, or the archive containing it.</summary>
    private string? CurrentContainerOrFile => CurrentFilePath ?? (_source is ArchiveSource a && !_detached ? a.Location : null);

    public TextStyle CurrentTextStyle => new(Settings.TextFontFamily, Settings.TextFontSize, Settings.TextBold,
        Settings.TextItalic, ParseColor(Settings.TextColor, Colors.Red));

    public static Color ParseColor(string? text, Color fallback)
    {
        try { return text is null ? fallback : (Color)ColorConverter.ConvertFromString(text); }
        catch (FormatException) { return fallback; }
    }

    // ---- Opening ----

    public async Task OpenPathAsync(string path)
    {
        if (WaitingForWork()) return;
        // Relative paths (command line "pViewer comic.cbz" or "pViewer .") would break the folder
        // comparisons, the sibling volumes and the saved LastFolder.
        try { path = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _view.ShowError($"Invalid path:\n{path}");
            return;
        }
        // Checked before asking about unsaved changes: nothing to discard for a file we can't open.
        bool isFolder = Directory.Exists(path);
        if (!isFolder && !File.Exists(path))
        {
            _view.ShowError($"Path not found:\n{path}");
            return;
        }
        if (!isFolder && !ImageFormats.IsArchive(path) && !ImageFormats.IsImage(path))
        {
            _view.ShowError($"«{Path.GetFileName(path)}» is not a supported format.");
            return;
        }
        if (!await ConfirmDiscardEditsAsync()) return;
        StopSlideshow();
        try
        {
            if (isFolder)
            {
                var folder = FolderSource.Open(path);
                if (folder.Pages.Count > 0)
                {
                    Settings.LastFolder = path;
                    Layout = PageLayout.Single; // two-page modes are chosen per archive (see ArchiveLayout)
                    await SetSourceAsync(folder, 0, null);
                    return;
                }
                folder.Dispose();
                var archives = ArchiveSource.Siblings(Path.Combine(path, "x.zip")).Where(File.Exists).ToList();
                if (archives.Count > 0) { await OpenArchiveAsync(archives[0], false, keepLayout: false); return; }
                _view.ShowError("The folder contains no images or archives.");
            }
            else if (ImageFormats.IsArchive(path))
            {
                await OpenArchiveAsync(path, false, keepLayout: false);
            }
            else
            {
                string folder = Path.GetDirectoryName(path)!;
                Settings.LastFolder = folder;
                var source = FolderSource.Open(folder);
                StartupTrace.Mark("folder listed");
                int index = source.IndexOf(path);
                if (index < 0) index = 0; // hidden file or changed list: start from the beginning
                Layout = PageLayout.Single;
                await SetSourceAsync(source, index, null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _view.ShowError(ex.Message);
        }
    }

    private async Task OpenArchiveAsync(string path, bool startAtEnd, bool keepLayout)
    {
        int request = ++_requestId;
        IsLoading = true;
        ErrorText = null;
        _archiveOpenRequest = request;
        // The changes were already confirmed or discarded by the caller: no undo or new edits on
        // the old image while the archive opens. A pasted image stays "detached", so file commands
        // (wallpaper, open with, properties) do not act on the folder page hidden behind it.
        _edit = null;
        _lastRectangle = null;
        Tool = ViewerTool.None;
        UpdateEditFlags();
        UpdateInfo();
        ArchiveSource archive;
        try
        {
            archive = await Task.Run(() => ArchiveSource.Open(path));
        }
        catch (Exception ex)
        {
            if (request != _requestId) return;
            IsLoading = false;
            StopSlideshow(); // otherwise the error would come back on every tick
            // Remembered, so moving between volumes skips it instead of trying it again forever.
            _brokenArchives.Add(path);
            _view.ShowError($"Cannot open the archive «{Path.GetFileName(path)}».\n{ex.Message}");
            // The open may have interrupted the loading of the current page (or cleared its error):
            // show it again, so what is on screen matches the position and the title.
            if (request != _requestId) return;
            if (_source is not null) await ShowCurrentAsync();
            else
            {
                // Only a pasted image could be on screen, and its changes were given up above.
                ResetEditState();
                _view.ClearPages();
                HasImage = false;
                UpdateInfo();
            }
            return;
        }
        finally
        {
            if (_archiveOpenRequest == request) _archiveOpenRequest = -1;
        }
        // Something else was opened meanwhile (a folder dropped, another archive): this one is stale.
        if (request != _requestId)
        {
            archive.Dispose();
            return;
        }
        _brokenArchives.Remove(path); // repaired since the last attempt
        Settings.LastFolder = Path.GetDirectoryName(path);
        _archiveSiblings = ArchiveSource.Siblings(path);
        _archiveIndex = _archiveSiblings.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (!keepLayout) Layout = Settings.ArchiveLayout;
        _nav.Layout = Layout;
        int start = 0;
        if (startAtEnd)
        {
            _nav.Reset(archive.Pages.Count);
            start = _nav.LastPosition();
        }
        await SetSourceAsync(archive, start, _archiveIndex);
    }

    private async Task SetSourceAsync(IImageSource source, int index, int? archiveIndex)
    {
        if (_disposed)
        {
            // Finished opening after the window closed (e.g. a slow archive).
            source.Dispose();
            return;
        }
        _cache?.Dispose();
        if (!ReferenceEquals(_source, source)) _source?.Dispose();
        _source = source;
        _cache = new PageCache(source, Settings.AutoRotateExif);
        if (archiveIndex is null) { _archiveSiblings = []; _archiveIndex = -1; }
        _nav.Layout = Layout;
        _nav.Reset(source.Pages.Count, index);
        _nav.HasNextContainer = _archiveIndex >= 0 && _archiveIndex < _archiveSiblings.Count - 1;
        _nav.HasPreviousContainer = _archiveIndex > 0;
        ResetEditState();
        await ShowCurrentAsync();
    }

    private void ResetEditState()
    {
        _edit = null;
        _detached = false;
        _lastRectangle = null;
        Tool = ViewerTool.None;
        UpdateEditFlags();
    }

    /// <summary>Shows the pages at the current position, with an instant preview if needed.</summary>
    private async Task ShowCurrentAsync()
    {
        if (_disposed || _source is null || _cache is null) return;
        int request = ++_requestId;
        var cache = _cache;
        int[] indices = _nav.VisibleIndices();
        ErrorText = null;
        ResetEditState();

        if (indices.Length == 0)
        {
            _visible = null;
            HasImage = false;
            IsLoading = false;
            _view.ClearPages();
            UpdateInfo();
            return;
        }

        // Drop (and cancel) pages passed while scrolling fast, before loading the new ones.
        cache.Trim(indices.Concat(_nav.PrefetchIndices()));

        var loaded = new LoadedImage[indices.Length];
        bool ready = true;
        for (int i = 0; i < indices.Length; i++)
        {
            if (cache.TryGetLoaded(indices[i], out var img)) loaded[i] = img!;
            else ready = false;
        }

        if (!ready)
        {
            // The previous page stays on screen until this one arrives, but nothing may edit, copy
            // or save it under the new page's name.
            _visible = null;
            IsLoading = true;
            UpdateInfo(pending: true);
            if (indices.Length == 1) _ = ShowPreviewAsync(cache.Get(indices[0]), request);
            try
            {
                for (int i = 0; i < indices.Length; i++)
                    loaded[i] = await cache.Get(indices[i]).Image;
            }
            catch (Exception ex)
            {
                if (_disposed) return;
                if (request != _requestId) return;
                IsLoading = false;
                _visible = null;
                HasImage = false;
                _view.ClearPages();
                ErrorText = ex is ImageDecodeException ? ex.Message
                    : $"Cannot open «{_source.Pages[indices[0]].Name}».\n{ex.Message}";
                UpdateInfo();
                PrefetchAround();
                return;
            }
            if (request != _requestId) return;
        }

        IsLoading = false;
        _visible = loaded;
        HasImage = true;
        _view.ShowPages(loaded.Select(l => l.Bitmap).ToList(), preserveView: false, loaded.Select(l => l.Animation).ToList());
        UpdateInfo();
        PrefetchAround();
    }

    private async Task ShowPreviewAsync(PageCache.Entry entry, int request)
    {
        try
        {
            byte[] bytes = await entry.Bytes;
            if (bytes.Length < PreviewThresholdBytes || entry.Image.IsCompleted || request != _requestId) return;
            bool orient = Settings.AutoRotateExif;
            var preview = await Task.Run(() => ImageDecoder.TryDecodeEmbeddedPreview(bytes, orient));
            if (preview is null || entry.Image.IsCompleted || request != _requestId) return;
            HasImage = true;
            _view.ShowPreview(preview.Value.Thumbnail, preview.Value.FullWidth, preview.Value.FullHeight);
        }
        catch (Exception)
        {
            // The preview is only an optimization.
        }
    }

    private void PrefetchAround()
    {
        if (_cache is null) return;
        var prefetch = _nav.PrefetchIndices();
        _cache.Prefetch(prefetch);
        _cache.Trim(_nav.VisibleIndices().Concat(prefetch));
    }

    // ---- Information ----

    public void SetZoom(double zoom) => ZoomText = HasImage ? $"{zoom * 100:0}%" : "";

    private void UpdateInfo(bool pending = false)
    {
        string modified = IsModified ? "● " : "";
        if (_detached)
        {
            Title = $"{modified}{_detachedName} — pViewer";
            StatusName = _detachedName;
            var bmp = _edit?.Current;
            StatusInfo = bmp is null ? "" : $"{bmp.PixelWidth} × {bmp.PixelHeight}";
            return;
        }
        if (_source is null || _nav.Count == 0)
        {
            Title = "pViewer";
            StatusName = _source is null ? "" : "No images";
            StatusInfo = "";
            return;
        }

        int[] indices = _nav.VisibleIndices();
        var names = indices.OrderBy(i => i).Select(i => _source.Pages[i].Name).ToList();
        string display = string.Join("  |  ", names.Select(n => Path.GetFileName(n.Replace('/', '\\'))));
        string container = _source is ArchiveSource ? $"  —  {Path.GetFileName(_source.Location)}" : "";
        Title = $"{modified}{display}{container} — pViewer";
        StatusName = display + container;

        var parts = new List<string>();
        var bitmap = _edit?.Current;
        if (bitmap is not null) parts.Add($"{bitmap.PixelWidth} × {bitmap.PixelHeight}");
        else if (_visible is { Length: 1 } v && !pending)
        {
            parts.Add($"{v[0].PixelWidth} × {v[0].PixelHeight}");
            if (!string.IsNullOrEmpty(v[0].FormatName)) parts.Add(v[0].FormatName);
            if (v[0].Animation is { } anim)
                parts.Add($"{anim.Frames.Count} frames, {anim.Delays.Sum(d => d.TotalSeconds):0.#} s");
            if (v[0].PageCount > 1) parts.Add($"page 1 of {v[0].PageCount}");
            parts.Add(FormatSize(v[0].FileSize));
        }
        int first = indices.Min() + 1, last = indices.Max() + 1;
        parts.Add(first == last ? $"{first} / {_nav.Count}" : $"{first}-{last} / {_nav.Count}");
        if (Layout != PageLayout.Single) parts.Add(Layout == PageLayout.Manga ? "Manga" : "Comic");
        StatusInfo = string.Join("   ·   ", parts);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.##} MB",
    };

    private void Toast(string message)
    {
        ToastText = message;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---- Navigation ----

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Next()
    {
        if (!CanNavigate || !await ConfirmDiscardEditsAsync()) return;
        await HandleNavigation(_nav.Next());
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Previous()
    {
        if (!CanNavigate || !await ConfirmDiscardEditsAsync()) return;
        await HandleNavigation(_nav.Previous());
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task First()
    {
        if (!CanNavigate || !await ConfirmDiscardEditsAsync()) return;
        _nav.First();
        await ShowCurrentAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Last()
    {
        if (!CanNavigate || !await ConfirmDiscardEditsAsync()) return;
        _nav.Last();
        await ShowCurrentAsync();
    }

    private async Task HandleNavigation(NavigationResult result)
    {
        switch (result)
        {
            case NavigationResult.Moved:
            case NavigationResult.Wrapped:
                await ShowCurrentAsync();
                break;
            case NavigationResult.NextContainer:
            case NavigationResult.PreviousContainer:
                int step = result == NavigationResult.NextContainer ? 1 : -1;
                int volume = _archiveIndex + step;
                while (volume >= 0 && volume < _archiveSiblings.Count && _brokenArchives.Contains(_archiveSiblings[volume]))
                    volume += step; // skip volumes that already failed to open
                if (volume < 0 || volume >= _archiveSiblings.Count)
                {
                    Toast(step > 0 ? "No further volume can be opened" : "No previous volume can be opened");
                    break;
                }
                Toast($"{(step > 0 ? "Next" : "Previous")} volume: {Path.GetFileName(_archiveSiblings[volume])}");
                await OpenArchiveAsync(_archiveSiblings[volume], startAtEnd: step < 0, keepLayout: true);
                break;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SetLayout(PageLayout layout)
    {
        if (!CanNavigate || !await ConfirmDiscardEditsAsync()) return;
        // Repeating the active mode's command goes back to single page (like the M and C keys).
        if (layout == Layout && layout != PageLayout.Single) layout = PageLayout.Single;
        Layout = layout;
        _nav.Layout = layout;
        await ShowCurrentAsync();
    }

    /// <summary>Shifts the pair by one page (F12), to realign double-page spreads.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ShiftPage(string deltaText)
    {
        int delta = int.Parse(deltaText);
        if (!CanNavigate || Layout == PageLayout.Single || !await ConfirmDiscardEditsAsync()) return;
        if (_nav.Shift(delta)) await ShowCurrentAsync();
    }

    /// <summary>F5: rescans the folder and reloads the image from disk, discarding the changes.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Reload()
    {
        if (!CanNavigate) return;
        // A pasted or joined image has no "original" to go back to: its edits are really lost.
        if (_detached && !await ConfirmDiscardEditsAsync()) return;
        if (_source is FolderSource folder)
        {
            string? current = _nav.Count > 0 ? folder.Pages[_nav.Position].FilePath : null;
            FolderSource fresh;
            try { fresh = FolderSource.Open(folder.Location); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Folder renamed, deleted or on a drive that was unplugged.
                _view.ShowError($"Cannot read the folder «{folder.Location}».\n{ex.Message}");
                return;
            }
            int index = current is null ? _nav.Position : fresh.IndexOf(current);
            await SetSourceAsync(fresh, index < 0 ? Math.Min(_nav.Position, Math.Max(0, fresh.Pages.Count - 1)) : index, null);
        }
        else
        {
            foreach (int i in _nav.VisibleIndices()) _cache?.Invalidate(i);
            await ShowCurrentAsync();
        }
    }

    // ---- Slideshow ----

    [RelayCommand]
    private void StartSlideshow(string secondsText)
    {
        double seconds = double.Parse(secondsText, System.Globalization.CultureInfo.InvariantCulture);
        if (!HasImage || _source is null) return;
        if (seconds <= 0)
        {
            string? text = _view.AskText("Slideshow", "Seconds between images:",
                Settings.SlideshowSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0.5 && v <= 3600 ? null : "Enter a number between 0.5 and 3600.");
            if (text is null) return;
            seconds = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            Settings.SlideshowSeconds = seconds;
        }
        _slideshowTimer.Interval = TimeSpan.FromSeconds(seconds);
        _slideshowTimer.Start();
        IsSlideshowRunning = true;
        if (!_view.IsFullscreen) _view.SetFullscreen(true);
        string esc = KeyList("Escape");
        Toast($"Slideshow every {seconds:0.#} s — {(esc.Length > 0 ? esc + " or click" : "click")} to stop");
    }

    /// <summary>"Esc or R": the current keys of the commands, for hints ("" if none has a key).</summary>
    private string KeyList(params string[] ids) =>
        string.Join(" or ", ids.Select(id => Hotkeys.DisplayText(id, Settings.Hotkeys)).Where(k => k.Length > 0));

    public void StopSlideshow()
    {
        if (!IsSlideshowRunning) return;
        _slideshowTimer.Stop();
        IsSlideshowRunning = false;
    }

    private async void SlideshowTick()
    {
        // While editing, loading (slow images must not be skipped) or with a dialog open the slideshow waits.
        if (IsModified || IsBusy || IsLoading || IsOpeningArchive || _view.HasModalDialog)
            return;
        await HandleNavigation(_nav.Next());
    }

    // ---- Editing ----

    private void UpdateEditFlags()
    {
        CanUndo = _edit?.CanUndo ?? false;
        CanRedo = _edit?.CanRedo ?? false;
        IsModified = _edit?.IsModified ?? false;
    }

    /// <summary>Creates (if needed) the edit session on the visible image. Page pairs are joined.</summary>
    private EditSession? EnsureEditSession()
    {
        if (IsOpeningArchive) return null; // the image on screen is about to be replaced
        if (_edit is not null) return _edit;
        if (_visible is null || _visible.Length == 0 || IsLoading) return null;
        if (_visible.Length == 1)
        {
            _edit = new EditSession(_visible[0].Bitmap);
            if (_visible[0].IsAnimated)
                Toast("Edits apply to the first frame: the animation will not be saved");
            else if (_visible[0].PageCount > 1)
                Toast($"This file has {_visible[0].PageCount} pages: edits apply to the first page only");
        }
        else
        {
            var composed = ImageOps.Compose(_visible.Select(v => v.Bitmap).ToList(), Colors.White);
            _edit = new EditSession(composed);
            _detached = true;
            _detachedName = "Joined pages";
            Toast("The two pages have been joined into a single image");
        }
        return _edit;
    }

    private int _pendingWork;
    /// <summary>A save is queued or running: a second Ctrl+S must not ask again or save twice.</summary>
    private int _savesQueued;
    /// <summary>Work items queued so far, and the count when the last save was queued: an edit queued
    /// after that save still needs its own.</summary>
    private int _queuedTotal, _queuedAtLastSave;

    /// <summary>Opening or pasting another image waits for running edits and saves (they belong to this one).</summary>
    public bool WaitingForWork()
    {
        if (!IsBusy) return false;
        Toast("Please wait: an edit or a save is still running");
        return true;
    }

    /// <summary>
    /// Runs edits and saves one at a time, in request order. IsBusy stays true while anything is
    /// queued, so navigation and undo cannot slip in between (e.g. pressing ↑ twice rotates twice).
    /// </summary>
    private async Task<T> RunExclusiveAsync<T>(Func<Task<T>> work)
    {
        _pendingWork++;
        _queuedTotal++;
        IsBusy = true;
        await _editGate.WaitAsync();
        try
        {
            return await work();
        }
        finally
        {
            _editGate.Release();
            if (--_pendingWork == 0) IsBusy = false;
        }
    }

    /// <param name="movesPixels">Rotations, flips, crops, resizes, borders: the last rectangle no
    /// longer marks the same area, so Tab must not fill it.</param>
    private Task<bool> ApplyEditAsync(Func<BitmapSource, BitmapSource> operation, bool needsSta = false, bool movesPixels = false)
    {
        // The edit belongs to the image shown when it was requested: if another image is shown by
        // the time its turn comes (paste, open…), it is dropped instead of landing on that one.
        int request = _requestId;
        return RunExclusiveAsync(async () =>
        {
            if (request != _requestId) return false;
            var session = EnsureEditSession();
            if (session is null) return false;
            var source = session.Current;
            try
            {
                var result = needsSta ? await StaTask.Run(() => operation(source)) : await Task.Run(() => operation(source));
                if (!ReferenceEquals(session, _edit)) return false;
                session.Apply(result);
                if (movesPixels) _lastRectangle = null;
                bool sameSize = result.PixelWidth == source.PixelWidth && result.PixelHeight == source.PixelHeight;
                _view.ShowPages([session.Current], preserveView: sameSize);
                UpdateEditFlags();
                UpdateInfo();
                return true;
            }
            catch (Exception ex)
            {
                _view.ShowError($"The operation failed.\n{ex.Message}");
                return false;
            }
        });
    }

    [RelayCommand]
    private void Undo()
    {
        if (IsBusy || _edit is null || !_edit.Undo()) return;
        _lastRectangle = null;
        ShowEditCurrent();
    }

    [RelayCommand]
    private void Redo()
    {
        if (IsBusy || _edit is null || !_edit.Redo()) return;
        _lastRectangle = null;
        ShowEditCurrent();
    }

    /// <summary>Shows the current edit state; back at the original of an animation, restarts it.</summary>
    private void ShowEditCurrent()
    {
        var current = _edit!.Current;
        var animation = !_detached && _visible is { Length: 1 } v && ReferenceEquals(current, v[0].Bitmap) ? v[0].Animation : null;
        _view.ShowPages([current], preserveView: true, animation is null ? null : [animation]);
        UpdateEditFlags();
        UpdateInfo();
    }

    [RelayCommand(AllowConcurrentExecutions = true)] private Task Rotate(string degrees) => ApplyEditAsync(b => ImageOps.Rotate(b, (int.Parse(degrees) + 360) % 360), movesPixels: true);
    [RelayCommand(AllowConcurrentExecutions = true)] private Task Flip(string direction) => ApplyEditAsync(b => ImageOps.Flip(b, direction == "H"), movesPixels: true);
    [RelayCommand(AllowConcurrentExecutions = true)] private Task Invert() => ApplyEditAsync(ImageOps.Invert);
    [RelayCommand(AllowConcurrentExecutions = true)] private Task Grayscale() => ApplyEditAsync(ImageOps.Grayscale);
    [RelayCommand(AllowConcurrentExecutions = true)] private Task Sepia() => ApplyEditAsync(ImageOps.Sepia);
    [RelayCommand(AllowConcurrentExecutions = true)] private Task BlackWhite() => ApplyEditAsync(ImageOps.BlackWhite);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task AddBorder(string color) => ApplyEditAsync(b =>
        ImageOps.AddBorder(b, Math.Max(1, Settings.BorderThickness), color == "White" ? Colors.White : Colors.Black), movesPixels: true);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Resize(string percentText)
    {
        int percent = int.Parse(percentText);
        if (percent > 0)
        {
            // Sizes computed when the edit runs: a queued edit may have changed them.
            await ApplyEditAsync(b => ImageOps.Resize(b,
                Math.Max(1, b.PixelWidth * percent / 100), Math.Max(1, b.PixelHeight * percent / 100)), movesPixels: true);
            return;
        }
        // The dialog shows the current size: a queued edit could still change it.
        if (WaitingForWork()) return;
        // No edit session before the dialog: cancelling must not join a page pair.
        var current = CurrentBitmapForExport();
        if (current is null) return;
        var size = _view.ShowResizeDialog(current.PixelWidth, current.PixelHeight);
        if (size is null) return;
        var (w, h) = size.Value;
        await ApplyEditAsync(b => ImageOps.Resize(b, w, h), movesPixels: true);
    }

    [RelayCommand]
    private void ToggleRedEyeTool()
    {
        if (!HasImage) return;
        Tool = Tool == ViewerTool.RedEye ? ViewerTool.None : ViewerTool.RedEye;
        Toast(Tool == ViewerTool.RedEye
            ? "Red-eye: drag a rectangle around each eye" + (KeyList("RedEye", "Escape") is { Length: > 0 } keys ? $" ({keys} to exit)" : "")
            : "Red-eye correction off");
    }

    private Int32Rect? _lastRectangle;

    /// <summary>Selections made with the mouse in the viewer.</summary>
    public async Task OnSelectionAsync(SelectionKind kind, Int32Rect rect)
    {
        // The rectangle is in the coordinates of the image on screen: a queued rotation or crop
        // would move it onto another area.
        if (WaitingForWork()) return;
        switch (kind)
        {
            case SelectionKind.Crop:
                await ApplyEditAsync(b => ImageOps.Crop(b, rect), movesPixels: true);
                break;
            case SelectionKind.Rectangle:
                var color = ParseColor(Settings.RectangleColor, Colors.Red);
                // Remembered for Tab only once drawn: a dropped edit (the image changed) must not leave it.
                if (await ApplyEditAsync(b => ImageOps.DrawRectangle(b, rect, color, Settings.RectangleThickness, fill: false), needsSta: true))
                    _lastRectangle = rect;
                break;
            case SelectionKind.RedEye:
                await ApplyEditAsync(b => ImageOps.RedEye(b, rect));
                break;
        }
    }

    /// <summary>Tab: fills the last drawn rectangle (handy to hide data in screenshots).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task FillLastRectangle()
    {
        if (_lastRectangle is not { } rect || _edit is null || WaitingForWork()) return;
        var color = ParseColor(Settings.RectangleColor, Colors.Red);
        await ApplyEditAsync(b => ImageOps.DrawRectangle(b, rect, color, 0, fill: true), needsSta: true);
    }

    public Task OnTextCommittedAsync(TextPlacement text) => ApplyEditAsync(b => ImageOps.DrawText(b, text), needsSta: true);

    /// <summary>Opens the font/color dialog and saves the choice as the default.</summary>
    public TextStyle? ChooseTextStyle()
    {
        var chosen = _view.ShowTextStyleDialog(CurrentTextStyle);
        if (chosen is null) return null;
        Settings.TextFontFamily = chosen.FontFamily;
        Settings.TextFontSize = chosen.FontSize;
        Settings.TextBold = chosen.Bold;
        Settings.TextItalic = chosen.Italic;
        Settings.TextColor = chosen.Color.ToString();
        return chosen;
    }

    public bool CanEdit => HasImage && !IsLoading && !IsBusy;

    // ---- Effects with preview ----

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Effect(string name)
    {
        // No edit session before the dialog: cancelling must not join a page pair.
        var current = IsBusy ? null : CurrentBitmapForExport();
        if (current is null) return;
        var editState = _edit?.Current;
        EffectDefinition effect = name switch
        {
            "BrightnessContrast" => new("Brightness and contrast",
                [new("Brightness", -100, 100, 0), new("Contrast", -100, 100, 0)],
                (b, v, _) => ImageOps.BrightnessContrast(b, v[0], v[1])),
            "Sharpen" => new("Sharpen",
                [new("Amount", 0, 10, 1.5, 0.1)],
                (b, v, s) => ImageOps.Sharpen(b, v[0] * s)),
            "Blur" => new("Blur",
                [new("Radius", 0, 40, 3, 0.5, " px")],
                (b, v, s) => ImageOps.Blur(b, v[0] * s)),
            "HueSaturation" => new("Hue and saturation",
                [new("Hue", -180, 180, 0, 1, "°"), new("Saturation", -100, 100, 0)],
                (b, v, _) => ImageOps.HueSaturation(b, v[0], v[1])),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        // The reduced preview copy is made in the background: on a very large image it takes a while.
        int request = _requestId;
        BitmapSource preview;
        try
        {
            var (maxW, maxH) = Views.EffectDialog.PreviewSize;
            preview = await Task.Run(() => ImageOps.FitWithin(current, maxW, maxH));
        }
        catch (Exception ex)
        {
            _view.ShowError($"The operation failed.\n{ex.Message}");
            return;
        }
        // The image changed meanwhile (another image, or an edit queued while the copy was made).
        if (request != _requestId || IsBusy || !ReferenceEquals(_edit?.Current, editState)) return;
        var values = _view.ShowEffectDialog(effect, preview, (double)preview.PixelWidth / current.PixelWidth);
        if (values is null) return;
        await ApplyEditAsync(b => effect.Apply(b, values, 1.0));
    }

    // ---- Clipboard ----

    [RelayCommand]
    private void Copy()
    {
        var bmp = CurrentBitmapForExport();
        if (bmp is null) return;
        try
        {
            ClipboardImage.Set(bmp);
            Toast("Image copied to the clipboard");
        }
        catch (Exception ex)
        {
            _view.ShowError($"Cannot copy to the clipboard.\n{ex.Message}");
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Paste()
    {
        if (WaitingForWork()) return;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count > 0) { await OpenPathAsync(files[0]!); return; }
            }
            var bitmap = ClipboardImage.Get();
            if (bitmap is null)
            {
                Toast("The clipboard contains no image");
                return;
            }
            if (!await ConfirmDiscardEditsAsync()) return;
            StopSlideshow();
            ++_requestId;
            ErrorText = null;
            IsLoading = false;
            _visible = null;
            _edit = new EditSession(bitmap);
            _detached = true;
            _detachedName = "Clipboard image";
            _lastRectangle = null;
            Tool = ViewerTool.None;
            HasImage = true;
            _view.ShowPages([bitmap], preserveView: false);
            UpdateEditFlags();
            UpdateInfo();
        }
        catch (Exception ex)
        {
            _view.ShowError($"Cannot read the clipboard.\n{ex.Message}");
        }
    }

    /// <summary>The image as it is seen (with edits, or the two pages joined).</summary>
    private BitmapSource? CurrentBitmapForExport()
    {
        if (_edit is not null) return _edit.Current;
        if (_visible is null || _visible.Length == 0 || IsLoading) return null;
        return _visible.Length == 1 ? _visible[0].Bitmap : ImageOps.Compose(_visible.Select(v => v.Bitmap).ToList(), Colors.White);
    }

    // ---- Saving ----

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Save() => await SaveCoreAsync();

    /// <returns>false if the user cancelled or saving failed.</returns>
    private async Task<bool> SaveCoreAsync(bool openSaved = true)
    {
        if (!HasImage) return false;
        // Before any redirect to Save As too (pasted image, archive page): no second dialog.
        if (_savesQueued > 0 && _queuedTotal == _queuedAtLastSave)
        {
            Toast("Already saving…");
            return true;
        }
        string? path = CurrentFilePath;
        if (_detached || path is null || !IsSingleFilePage || !ImageFormats.CanSave(path)) return await SaveAsCoreAsync(openSaved);
        // With edits still queued (e.g. ↑ then Ctrl+S right away) the image is not modified yet:
        // the save is queued after them and checks again when its turn comes.
        if (!IsBusy && (_edit is null || !_edit.IsModified))
        {
            Toast("Nothing to save");
            return true;
        }
        // Overwriting a multi-page file (TIFF) would keep only the page shown: save under a new name.
        if (_visible is { Length: 1 } pages && pages[0].PageCount > 1)
        {
            Toast($"«{Path.GetFileName(path)}» has {pages[0].PageCount} pages: choose a new name to keep the original");
            return await SaveAsCoreAsync(openSaved);
        }
        // The same for an animation: overwriting it would leave only the edited first frame.
        if (_visible is { Length: 1 } anim && anim[0].IsAnimated)
        {
            Toast($"«{Path.GetFileName(path)}» is animated: choose a new name to keep the original");
            return await SaveAsCoreAsync(openSaved);
        }
        if (Settings.ConfirmOverwrite)
        {
            var answer = _view.Ask($"Overwrite «{Path.GetFileName(path)}» with your changes?\n\nChoose «No» to save under another name.",
                "Save", MessageBoxButton.YesNoCancel);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.No) return await SaveAsCoreAsync(openSaved);
        }
        return await WriteAsync(() => _edit is { IsModified: true } edit ? edit.Current : null, path);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SaveAs() => await SaveAsCoreAsync();

    private async Task<bool> SaveAsCoreAsync(bool openSaved = true)
    {
        var bitmap = CurrentBitmapForExport();
        if (bitmap is null) return false;
        string name = _detached || _source is null
            ? $"{_detachedName} {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png"
            : Path.GetFileName(_source.Pages[_nav.Position].Name.Replace('/', '\\'));
        if (!ImageFormats.CanSave(name)) name = Path.ChangeExtension(name, ".png");
        string? dir = _source is null ? Settings.LastFolder
            : _source is ArchiveSource ? Path.GetDirectoryName(_source.Location) : _source.Location;
        string? path = _view.PickSaveFile(dir, name);
        if (path is null) return false;
        if (!ImageFormats.CanSave(path))
        {
            _view.ShowError("Unsupported format for saving: use jpg, png, webp, bmp, gif, tif or jxr.");
            return false;
        }
        bool animated = _visible?.Any(v => v.IsAnimated) == true;
        bool ok = await WriteAsync(CurrentBitmapForExport, path);
        if (!ok) return false;
        if (animated) Toast($"Saved: {Path.GetFileName(path)} (first frame of the animation only)");
        if (_source is FolderSource folder &&
            string.Equals(Path.GetDirectoryName(path), folder.Location, StringComparison.OrdinalIgnoreCase))
        {
            RefreshFolderKeepingPosition();
            // Saved into the folder being browsed: the saved file becomes the current one, so a
            // later Ctrl+S writes there and not over the original. Layout and position are kept.
            // (Not while other edits are queued: they belong to the image on screen.)
            if (openSaved && !IsBusy && _source is FolderSource current)
            {
                int index = current.IndexOf(Path.GetFullPath(path));
                if (index >= 0 && (index != _nav.Position || _detached))
                {
                    _nav.GoTo(index);
                    await ShowCurrentAsync();
                }
            }
        }
        else if (openSaved && _source is null && !IsBusy)
        {
            // A pasted image with nothing else open: show the file it was saved to.
            await OpenPathAsync(path);
        }
        // Saved elsewhere (an exported copy): browsing stays where it was. The edits count as
        // saved, so Ctrl+S has nothing to overwrite.
        return true;
    }

    /// <param name="getBitmap">
    /// Evaluated when the save actually runs, after any queued edit; null = nothing to save.
    /// </param>
    private Task<bool> WriteAsync(Func<BitmapSource?> getBitmap, string path)
    {
        var loaded = !_detached && _visible is { Length: 1 } ? _visible[0] : null;
        var meta = loaded?.JpegMetadata;
        var colorContexts = loaded?.ColorContexts;
        int quality = Settings.JpegQuality;
        // Pixels are upright only if they were decoded with auto-rotation (the setting may have
        // changed since): otherwise the original Orientation tag is kept.
        bool resetOrientation = loaded?.AutoOriented ?? true;
        // The save belongs to the image shown when it was requested: never write another image
        // (pasted, opened meanwhile…) over that file.
        int request = _requestId;
        _savesQueued++;
        var task = RunExclusiveAsync(async () =>
        {
            try
            {
                if (request != _requestId) return false;
                var bitmap = getBitmap();
                if (bitmap is null)
                {
                    Toast("Nothing to save");
                    return true;
                }
                var session = _edit;
                await StaTask.Run(() => { ImageSaver.Save(bitmap, path, quality, meta, resetOrientation, colorContexts); return true; });
                // Mark as saved only the state that was written (not one reached by a later undo).
                if (session is not null && ReferenceEquals(session.Current, bitmap)) session.MarkSaved();
                InvalidateCachedFile(path);
                UpdateEditFlags();
                UpdateInfo();
                Toast($"Saved: {Path.GetFileName(path)}");
                return true;
            }
            catch (Exception ex)
            {
                _view.ShowError($"Saving failed.\n{ex.Message}");
                return false;
            }
            finally
            {
                _savesQueued--;
            }
        });
        _queuedAtLastSave = _queuedTotal;
        return task;
    }

    /// <summary>A file of the current folder was rewritten: its cached decode is stale.</summary>
    private void InvalidateCachedFile(string path)
    {
        if (_source is FolderSource folder && _cache is not null)
        {
            int index = folder.IndexOf(Path.GetFullPath(path));
            if (index >= 0) _cache.Invalidate(index);
        }
    }

    /// <summary>Asks what to do with unsaved changes. false = the user cancelled.</summary>
    public async Task<bool> ConfirmDiscardEditsAsync()
    {
        if (_edit is null || !_edit.IsModified || !Settings.ConfirmDiscardEdits) return true;
        var answer = _view.Ask("The image has unsaved changes. Save them?", "Unsaved changes",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer != MessageBoxResult.Yes) return true;
        // Saving on the way to another image: a Save As must not also switch to the saved file.
        if (!await SaveCoreAsync(openSaved: false)) return false;
        // The window stays usable during the save: an edit made meanwhile is not saved, so stay.
        if (IsBusy || _edit is { IsModified: true })
        {
            Toast("The image was changed while saving: stay on it");
            return false;
        }
        return true;
    }

    // ---- File operations ----

    private void RefreshFolderKeepingPosition()
    {
        if (_source is not FolderSource folder || _cache is null) return;
        // The page under the view, even when the image is detached (joined pages, pasted image):
        // a file saved into the folder may shift the indices.
        string? current = _nav.Count > 0 ? folder.Pages[_nav.Position].FilePath : null;
        var fresh = FolderSource.Open(folder.Location);
        // Same list: no need to rebuild the cache.
        if (fresh.Pages.Select(p => p.FilePath).SequenceEqual(folder.Pages.Select(p => p.FilePath), StringComparer.OrdinalIgnoreCase))
            return;
        _cache.Dispose();
        _source.Dispose();
        _source = fresh;
        _cache = new PageCache(fresh, Settings.AutoRotateExif);
        int index = current is null ? -1 : fresh.IndexOf(current);
        _nav.Reset(fresh.Pages.Count, index < 0 ? _nav.Position : index);
        UpdateInfo();
        // A page was loading from the old cache (now cancelled): load it again from the new one.
        if (IsLoading && !IsOpeningArchive) _ = ShowCurrentAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Delete()
    {
        if (IsBusy || IsOpeningArchive) return; // the archive being opened would replace the folder mid-operation
        string? path = CurrentFilePath;
        if (!IsSingleFilePage || path is null)
        {
            _view.ShowError("Only an image file shown on its own can be deleted (not a page pair, an archive or a clipboard image).");
            return;
        }
        if (Settings.ConfirmDelete)
        {
            string where = Settings.DeleteToRecycleBin ? "Move to the Recycle Bin" : "Permanently delete";
            if (_view.Ask($"{where} «{Path.GetFileName(path)}»?", "Delete", MessageBoxButton.YesNo,
                    Settings.DeleteToRecycleBin ? MessageBoxImage.Question : MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }
        try
        {
            Shell.DeleteFile(path, Settings.DeleteToRecycleBin, _view.WindowHandle);
        }
        catch (Exception ex)
        {
            _view.ShowError($"Cannot delete the file.\n{ex.Message}");
            return;
        }
        int position = _nav.Position;
        var fresh = FolderSource.Open(_source!.Location);
        await SetSourceAsync(fresh, Math.Min(position, Math.Max(0, fresh.Pages.Count - 1)), null);
        Toast(Settings.DeleteToRecycleBin ? "Moved to the Recycle Bin" : "File deleted");
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Rename()
    {
        if (IsBusy || IsOpeningArchive) return; // the archive being opened would replace the folder mid-operation
        string? path = CurrentFilePath;
        if (!IsSingleFilePage || path is null)
        {
            Toast(_detached ? "Not available for pasted or joined images" : "Only an image file shown on its own can be renamed");
            return;
        }
        if (!await ConfirmDiscardEditsAsync()) return;
        string dir = Path.GetDirectoryName(path)!;
        string ext = Path.GetExtension(path);
        string? name = _view.AskText("Rename", "New name:", Path.GetFileNameWithoutExtension(path), s =>
        {
            s = s.Trim();
            if (s.Length == 0 || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Invalid name.";
            string target = Path.Combine(dir, s + ext);
            if (File.Exists(target) && !string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
                return "A file with this name already exists.";
            return null;
        });
        if (name is null) return;
        string newPath = Path.Combine(dir, name.Trim() + ext);
        if (newPath == path) return;
        try
        {
            File.Move(path, newPath);
        }
        catch (Exception ex)
        {
            _view.ShowError($"Cannot rename the file.\n{ex.Message}");
            return;
        }
        var fresh = FolderSource.Open(dir);
        await SetSourceAsync(fresh, Math.Max(0, fresh.IndexOf(newPath)), null);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task BatchRename()
    {
        if (IsBusy || IsOpeningArchive) return; // the archive being opened would replace the folder mid-operation
        if (_source is not FolderSource folder || folder.Pages.Count == 0) return;
        if (!await ConfirmDiscardEditsAsync()) return;
        var options = _view.ShowBatchRenameDialog(Path.GetFileName(folder.Location), folder.Pages.Count);
        if (options is null) return;
        string? current = CurrentFilePath;
        Dictionary<string, string> renamed;
        try
        {
            renamed = BatchRenamer.Rename(folder.Pages.Select(p => p.FilePath!).ToList(), options);
        }
        catch (Exception ex)
        {
            _view.ShowError($"Renaming failed.\n{ex.Message}");
            renamed = [];
        }
        var fresh = FolderSource.Open(folder.Location);
        int index = current is not null && renamed.TryGetValue(current, out var np) ? fresh.IndexOf(np) : _nav.Position;
        await SetSourceAsync(fresh, Math.Max(0, index), null);
        if (renamed.Count > 0) Toast($"Renamed {renamed.Count} files");
    }

    [RelayCommand]
    private void Batch()
    {
        // An archive being opened (or a save refreshing the folder) would dispose the source while the batch reads it.
        if (_source is null || _source.Pages.Count == 0 || IsOpeningArchive || WaitingForWork()) return;
        _view.ShowBatchDialog(_source, Settings);
        if (_source is FolderSource) RefreshFolderKeepingPosition();
    }

    private void NotAFile() => Toast("Not available for pasted or joined images: save the image first");

    [RelayCommand]
    private void OpenWith()
    {
        string? path = CurrentContainerOrFile;
        if (path is not null) Shell.OpenWith(path);
        else NotAFile();
    }

    [RelayCommand]
    private void ShowInExplorer()
    {
        string? path = CurrentContainerOrFile;
        if (path is not null) Shell.ShowInExplorer(path);
        else NotAFile();
    }

    [RelayCommand]
    private void ShowProperties()
    {
        string? path = CurrentContainerOrFile;
        if (path is not null) Shell.ShowProperties(path, _view.WindowHandle);
        else NotAFile();
    }

    /// <summary>Formats Windows can show as wallpaper by itself; the others get a decoded PNG copy.</summary>
    private static bool WallpaperCanUse(string? path) =>
        path is not null && Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".png" or ".bmp" or ".dib" or ".gif" or ".tif" or ".tiff";

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SetWallpaper()
    {
        string? path = CurrentFilePath;
        try
        {
            if (path is null || !IsSingleFilePage || IsModified || !WallpaperCanUse(path))
            {
                var bmp = CurrentBitmapForExport();
                if (bmp is null) return;
                path = Path.Combine(Shell.LocalDataFolder, "wallpaper.png");
                string target = path;
                // In the edit/save queue: closing the window waits for it (no half-written file left).
                await RunExclusiveAsync(() => StaTask.Run(() => { ImageSaver.Save(bmp, target, 95); return true; }));
            }
            Shell.SetWallpaper(path);
            Toast("Desktop background set");
        }
        catch (Exception ex)
        {
            _view.ShowError($"Cannot set the desktop background.\n{ex.Message}");
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ShowMetadata()
    {
        if (_detached) { NotAFile(); return; }
        if (_cache is null || _source is null || _nav.Count == 0 || IsOpeningArchive) return; // also while the page is still loading
        int index = _nav.Position;
        int request = _requestId;
        string name = Path.GetFileName(_source!.Pages[index].Name);
        try
        {
            byte[] bytes = await _cache.Get(index).Bytes;
            var groups = await Task.Run(() => MetadataService.Read(bytes));
            if (request != _requestId) return; // another image is shown by now (slow solid archive)
            if (groups.Count == 0)
            {
                Toast("No metadata found");
                return;
            }
            _view.ShowMetadata(name, groups);
        }
        catch (OperationCanceledException) { /* the source changed while reading */ }
        catch (Exception ex)
        {
            if (request == _requestId) _view.ShowError($"Cannot read the metadata.\n{ex.Message}");
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenSettings()
    {
        bool autoOrient = Settings.AutoRotateExif;
        if (!_view.ShowSettingsDialog(Settings)) return;
        SettingsStore.Save(Settings);
        ViewMode = Settings.ViewMode;
        // Auto-rotation changed: cached images must be decoded again (also for the pages that come
        // after a pasted/joined image). The current page is reloaded only if that loses nothing.
        if (autoOrient != Settings.AutoRotateExif && _source is not null)
        {
            _cache?.Dispose();
            _cache = new PageCache(_source, Settings.AutoRotateExif);
            if (!IsOpeningArchive && (IsLoading || (!_detached && !IsBusy && (_edit is null || !_edit.IsModified))))
                await ShowCurrentAsync();
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Open()
    {
        string? path = _view.PickOpenFile(Settings.LastFolder);
        if (path is not null) await OpenPathAsync(path);
    }

    [RelayCommand]
    private void ToggleWhiteBackground() => WhiteBackground = !WhiteBackground;

    [RelayCommand]
    private void ChangeViewMode(ViewMode mode)
    {
        // Picking the active mode again must still undo a manual zoom.
        if (ViewMode == mode) _view.ResetView();
        else ViewMode = mode;
    }

    /// <summary>A: toggles between actual size and the preferred view mode.</summary>
    [RelayCommand]
    private void ToggleActualSize() =>
        ViewMode = ViewMode == ViewMode.ActualSize ? Settings.ViewMode : ViewMode.ActualSize;

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _slideshowTimer.Stop();
        _toastTimer.Stop();
        _cache?.Dispose();
        _source?.Dispose();
    }
}
