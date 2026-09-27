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
        if (!await ConfirmDiscardEditsAsync()) return;
        StopSlideshow();
        try
        {
            if (Directory.Exists(path))
            {
                var folder = FolderSource.Open(path);
                if (folder.Pages.Count > 0)
                {
                    Settings.LastFolder = path;
                    await SetSourceAsync(folder, 0, null);
                    return;
                }
                var archives = ArchiveSource.Siblings(Path.Combine(path, "x.zip")).Where(File.Exists).ToList();
                if (archives.Count > 0) { await OpenArchiveAsync(archives[0], false, keepLayout: false); return; }
                _view.ShowError("The folder contains no images or archives.");
            }
            else if (File.Exists(path) && ImageFormats.IsArchive(path))
            {
                await OpenArchiveAsync(path, false, keepLayout: false);
            }
            else if (File.Exists(path))
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
                Settings.LastFolder = folder;
                var source = FolderSource.Open(folder);
                int index = source.IndexOf(Path.GetFullPath(path));
                if (index < 0)
                {
                    if (!ImageFormats.IsImage(path))
                    {
                        _view.ShowError($"«{Path.GetFileName(path)}» is not a supported format.");
                        source.Dispose();
                        return;
                    }
                    index = 0; // hidden file or changed list: start from the beginning
                }
                await SetSourceAsync(source, index, null);
            }
            else
            {
                _view.ShowError($"Path not found:\n{path}");
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
        ArchiveSource archive;
        try
        {
            archive = await Task.Run(() => ArchiveSource.Open(path));
        }
        catch (Exception ex)
        {
            if (request != _requestId) return;
            IsLoading = false;
            _view.ShowError($"Cannot open the archive «{Path.GetFileName(path)}».\n{ex.Message}");
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
        if (_source is null || _cache is null) return;
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
                Toast($"Next volume: {Path.GetFileName(_archiveSiblings[_archiveIndex + 1])}");
                await OpenArchiveAsync(_archiveSiblings[_archiveIndex + 1], startAtEnd: false, keepLayout: true);
                break;
            case NavigationResult.PreviousContainer:
                Toast($"Previous volume: {Path.GetFileName(_archiveSiblings[_archiveIndex - 1])}");
                await OpenArchiveAsync(_archiveSiblings[_archiveIndex - 1], startAtEnd: true, keepLayout: true);
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
        if (_source is null) return;
        if (_source is FolderSource folder)
        {
            string? current = CurrentFilePath;
            var fresh = FolderSource.Open(folder.Location);
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
                Settings.SlideshowSeconds.ToString("0.#"),
                s => double.TryParse(s, out double v) && v >= 0.5 && v <= 3600 ? null : "Enter a number between 0.5 and 3600.");
            if (text is null) return;
            seconds = double.Parse(text);
            Settings.SlideshowSeconds = seconds;
        }
        _slideshowTimer.Interval = TimeSpan.FromSeconds(seconds);
        _slideshowTimer.Start();
        IsSlideshowRunning = true;
        if (!_view.IsFullscreen) _view.SetFullscreen(true);
        Toast($"Slideshow every {seconds:0.#} s — Esc or click to stop");
    }

    public void StopSlideshow()
    {
        if (!IsSlideshowRunning) return;
        _slideshowTimer.Stop();
        IsSlideshowRunning = false;
    }

    private async void SlideshowTick()
    {
        // While editing, loading (slow images must not be skipped) or with a dialog open the slideshow waits.
        if (IsModified || IsBusy || IsLoading || IsOpeningArchive || Application.Current.Windows.OfType<Window>().Any(w => w.IsActive && w != Application.Current.MainWindow))
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
        if (_edit is not null) return _edit;
        if (_visible is null || _visible.Length == 0 || IsLoading) return null;
        if (_visible.Length == 1)
        {
            _edit = new EditSession(_visible[0].Bitmap);
            if (_visible[0].Animation is not null)
                Toast("Edits apply to the first frame: the animation will not be saved");
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

    /// <summary>
    /// Runs edits and saves one at a time, in request order. IsBusy stays true while anything is
    /// queued, so navigation and undo cannot slip in between (e.g. pressing ↑ twice rotates twice).
    /// </summary>
    private async Task<T> RunExclusiveAsync<T>(Func<Task<T>> work)
    {
        _pendingWork++;
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

    private Task ApplyEditAsync(Func<BitmapSource, BitmapSource> operation, bool needsSta = false) =>
        RunExclusiveAsync(async () =>
        {
            var session = EnsureEditSession();
            if (session is null) return false;
            var source = session.Current;
            try
            {
                var result = needsSta ? await StaTask.Run(() => operation(source)) : await Task.Run(() => operation(source));
                if (!ReferenceEquals(session, _edit)) return false;
                session.Apply(result);
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

    [RelayCommand]
    private void Undo()
    {
        if (IsBusy || _edit is null || !_edit.Undo()) return;
        ShowEditCurrent();
    }

    [RelayCommand]
    private void Redo()
    {
        if (IsBusy || _edit is null || !_edit.Redo()) return;
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

    [RelayCommand(AllowConcurrentExecutions = true)] private Task Rotate(string degrees) => ApplyEditAsync(b => ImageOps.Rotate(b, (int.Parse(degrees) + 360) % 360));
    [RelayCommand(AllowConcurrentExecutions = true)] private Task Flip(string direction) => ApplyEditAsync(b => ImageOps.Flip(b, direction == "H"));
    [RelayCommand(AllowConcurrentExecutions = true)] private Task Invert() => ApplyEditAsync(ImageOps.Invert);
    [RelayCommand(AllowConcurrentExecutions = true)] private Task Grayscale() => ApplyEditAsync(ImageOps.Grayscale);
    [RelayCommand(AllowConcurrentExecutions = true)] private Task Sepia() => ApplyEditAsync(ImageOps.Sepia);
    [RelayCommand(AllowConcurrentExecutions = true)] private Task BlackWhite() => ApplyEditAsync(ImageOps.BlackWhite);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task AddBorder(string color) => ApplyEditAsync(b =>
        ImageOps.AddBorder(b, Math.Max(1, Settings.BorderThickness), color == "White" ? Colors.White : Colors.Black));

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Resize(string percentText)
    {
        int percent = int.Parse(percentText);
        if (percent > 0)
        {
            // Sizes computed when the edit runs: a queued edit may have changed them.
            await ApplyEditAsync(b => ImageOps.Resize(b,
                Math.Max(1, b.PixelWidth * percent / 100), Math.Max(1, b.PixelHeight * percent / 100)));
            return;
        }
        // No edit session before the dialog: cancelling must not join a page pair.
        var current = CurrentBitmapForExport();
        if (current is null) return;
        var size = _view.ShowResizeDialog(current.PixelWidth, current.PixelHeight);
        if (size is null) return;
        var (w, h) = size.Value;
        await ApplyEditAsync(b => ImageOps.Resize(b, w, h));
    }

    [RelayCommand]
    private void ToggleRedEyeTool()
    {
        if (!HasImage) return;
        Tool = Tool == ViewerTool.RedEye ? ViewerTool.None : ViewerTool.RedEye;
        Toast(Tool == ViewerTool.RedEye
            ? "Red-eye: drag a rectangle around each eye (R or Esc to exit)"
            : "Red-eye correction off");
    }

    private Int32Rect? _lastRectangle;

    /// <summary>Selections made with the mouse in the viewer.</summary>
    public async Task OnSelectionAsync(SelectionKind kind, Int32Rect rect)
    {
        switch (kind)
        {
            case SelectionKind.Crop:
                await ApplyEditAsync(b => ImageOps.Crop(b, rect));
                break;
            case SelectionKind.Rectangle:
                _lastRectangle = rect;
                var color = ParseColor(Settings.RectangleColor, Colors.Red);
                await ApplyEditAsync(b => ImageOps.DrawRectangle(b, rect, color, Settings.RectangleThickness, fill: false), needsSta: true);
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
        if (_lastRectangle is not { } rect || _edit is null) return;
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
        var values = _view.ShowEffectDialog(effect, current);
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
            Clipboard.SetImage(bmp);
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
    private async Task<bool> SaveCoreAsync()
    {
        if (!HasImage) return false;
        string? path = CurrentFilePath;
        if (_detached || path is null || !IsSingleFilePage || !ImageFormats.CanSave(path)) return await SaveAsCoreAsync();
        if (_edit is null || !_edit.IsModified)
        {
            Toast("Nothing to save");
            return true;
        }
        if (Settings.ConfirmOverwrite)
        {
            var answer = _view.Ask($"Overwrite «{Path.GetFileName(path)}» with your changes?\n\nChoose «No» to save under another name.",
                "Save", MessageBoxButton.YesNoCancel);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.No) return await SaveAsCoreAsync();
        }
        return await WriteAsync(() => _edit?.Current, path);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SaveAs() => await SaveAsCoreAsync();

    private async Task<bool> SaveAsCoreAsync()
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
        bool ok = await WriteAsync(CurrentBitmapForExport, path);
        if (ok && _edit is null && _visible?.Any(v => v.Animation is not null) == true)
            Toast($"Saved: {Path.GetFileName(path)} (first frame of the animation only)");
        if (ok && _source is FolderSource folder &&
            string.Equals(Path.GetDirectoryName(path), folder.Location, StringComparison.OrdinalIgnoreCase))
            RefreshFolderKeepingPosition();
        return ok;
    }

    /// <param name="getBitmap">Evaluated when the save actually runs, after any queued edit.</param>
    private Task<bool> WriteAsync(Func<BitmapSource?> getBitmap, string path)
    {
        var session = _edit;
        var meta = !_detached && _visible is { Length: 1 } ? _visible[0].JpegMetadata : null;
        int quality = Settings.JpegQuality;
        // Pixels are upright only if they were decoded with auto-rotation: otherwise keep the tag.
        bool resetOrientation = Settings.AutoRotateExif;
        return RunExclusiveAsync(async () =>
        {
            var bitmap = getBitmap();
            if (bitmap is null) return false;
            try
            {
                await StaTask.Run(() => { ImageSaver.Save(bitmap, path, quality, meta, resetOrientation); return true; });
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
        });
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
        return answer switch
        {
            MessageBoxResult.Cancel => false,
            MessageBoxResult.Yes => await SaveCoreAsync(),
            _ => true,
        };
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
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Delete()
    {
        if (IsBusy) return;
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
        if (IsBusy) return;
        string? path = CurrentFilePath;
        if (!IsSingleFilePage || path is null) return;
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
        if (IsBusy) return;
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
        if (_source is null || _source.Pages.Count == 0) return;
        _view.ShowBatchDialog(_source, Settings);
        if (_source is FolderSource) RefreshFolderKeepingPosition();
    }

    [RelayCommand]
    private void OpenWith()
    {
        string? path = CurrentContainerOrFile;
        if (path is not null) Shell.OpenWith(path);
    }

    [RelayCommand]
    private void ShowInExplorer()
    {
        string? path = CurrentContainerOrFile;
        if (path is not null) Shell.ShowInExplorer(path);
    }

    [RelayCommand]
    private void ShowProperties()
    {
        string? path = CurrentContainerOrFile;
        if (path is not null) Shell.ShowProperties(path, _view.WindowHandle);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SetWallpaper()
    {
        string? path = CurrentFilePath;
        try
        {
            if (path is null || !IsSingleFilePage || IsModified)
            {
                var bmp = CurrentBitmapForExport();
                if (bmp is null) return;
                path = Path.Combine(Shell.LocalDataFolder, "wallpaper.png");
                string target = path;
                await StaTask.Run(() => { ImageSaver.Save(bmp, target, 95); return true; });
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
        if (_cache is null || _detached || _visible is null) return;
        int index = _nav.Position;
        try
        {
            byte[] bytes = await _cache.Get(index).Bytes;
            var groups = await Task.Run(() => MetadataService.Read(bytes));
            if (groups.Count == 0)
            {
                Toast("No metadata found");
                return;
            }
            _view.ShowMetadata(Path.GetFileName(_source!.Pages[index].Name), groups);
        }
        catch (Exception ex)
        {
            _view.ShowError($"Cannot read the metadata.\n{ex.Message}");
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
            if (!_detached && !IsBusy && (_edit is null || !_edit.IsModified))
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

    public void Dispose()
    {
        _slideshowTimer.Stop();
        _cache?.Dispose();
        _source?.Dispose();
    }
}
