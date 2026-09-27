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

    /// <summary>Pagine caricate attualmente visibili (null se l'immagine è "staccata", es. dagli appunti).</summary>
    private LoadedImage[]? _visible;
    private EditSession? _edit;
    /// <summary>Immagine non legata a un file: incollata, oppure due pagine unite per modificarle.</summary>
    private bool _detached;
    private string _detachedName = "";
    private int _requestId;

    public MainViewModel(IMainView view, AppSettings settings)
    {
        _view = view;
        Settings = settings;
        _viewMode = settings.ViewMode;
        _slideshowTimer.Tick += (_, _) => SlideshowTick();
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastText = null; };
    }

    public AppSettings Settings { get; }

    // ---- Stato osservabile ----

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

    /// <summary>File su disco della pagina corrente (null per archivi e immagini staccate).</summary>
    public string? CurrentFilePath =>
        _detached || _source is null || _nav.Count == 0 ? null : _source.Pages[_nav.Position].FilePath;

    /// <summary>Il file "fisico" legato alla vista: la pagina, oppure l'archivio che la contiene.</summary>
    private string? CurrentContainerOrFile => CurrentFilePath ?? (_source is ArchiveSource a && !_detached ? a.Location : null);

    public TextStyle CurrentTextStyle => new(Settings.TextFontFamily, Settings.TextFontSize, Settings.TextBold,
        Settings.TextItalic, ParseColor(Settings.TextColor, Colors.Red));

    public static Color ParseColor(string? text, Color fallback)
    {
        try { return text is null ? fallback : (Color)ColorConverter.ConvertFromString(text); }
        catch (FormatException) { return fallback; }
    }

    // ---- Apertura ----

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
                _view.ShowError("La cartella non contiene immagini né archivi.");
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
                        _view.ShowError($"«{Path.GetFileName(path)}» non è un formato supportato.");
                        source.Dispose();
                        return;
                    }
                    index = 0; // file nascosto o elenco cambiato: si parte dall'inizio
                }
                await SetSourceAsync(source, index, null);
            }
            else
            {
                _view.ShowError($"Percorso non trovato:\n{path}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _view.ShowError(ex.Message);
        }
    }

    private async Task OpenArchiveAsync(string path, bool startAtEnd, bool keepLayout)
    {
        IsLoading = true;
        ErrorText = null;
        ArchiveSource archive;
        try
        {
            archive = await Task.Run(() => ArchiveSource.Open(path));
        }
        catch (Exception ex)
        {
            IsLoading = false;
            _view.ShowError($"Impossibile aprire l'archivio «{Path.GetFileName(path)}».\n{ex.Message}");
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
        Tool = ViewerTool.None;
        UpdateEditFlags();
    }

    /// <summary>Mostra le pagine della posizione corrente, con anteprima immediata se serve.</summary>
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
                    : $"Impossibile aprire «{_source.Pages[indices[0]].Name}».\n{ex.Message}";
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
            // L'anteprima è solo un'ottimizzazione.
        }
    }

    private void PrefetchAround()
    {
        if (_cache is null) return;
        var prefetch = _nav.PrefetchIndices();
        _cache.Prefetch(prefetch);
        _cache.Trim(_nav.VisibleIndices().Concat(prefetch));
    }

    // ---- Informazioni ----

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
            StatusName = _source is null ? "" : "Nessuna immagine";
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
                parts.Add($"{anim.Frames.Count} fotogrammi, {anim.Delays.Sum(d => d.TotalSeconds):0.#} s");
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

    // ---- Navigazione ----

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Next()
    {
        if (_source is null || !await ConfirmDiscardEditsAsync()) return;
        await HandleNavigation(_nav.Next());
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Previous()
    {
        if (_source is null || !await ConfirmDiscardEditsAsync()) return;
        await HandleNavigation(_nav.Previous());
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task First()
    {
        if (_source is null || !await ConfirmDiscardEditsAsync()) return;
        _nav.First();
        await ShowCurrentAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Last()
    {
        if (_source is null || !await ConfirmDiscardEditsAsync()) return;
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
                Toast($"Volume successivo: {Path.GetFileName(_archiveSiblings[_archiveIndex + 1])}");
                await OpenArchiveAsync(_archiveSiblings[_archiveIndex + 1], startAtEnd: false, keepLayout: true);
                break;
            case NavigationResult.PreviousContainer:
                Toast($"Volume precedente: {Path.GetFileName(_archiveSiblings[_archiveIndex - 1])}");
                await OpenArchiveAsync(_archiveSiblings[_archiveIndex - 1], startAtEnd: true, keepLayout: true);
                break;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SetLayout(PageLayout layout)
    {
        if (_source is null || !await ConfirmDiscardEditsAsync()) return;
        // Ripetere il comando della modalità attiva torna alla pagina singola (come i tasti M e C).
        if (layout == Layout && layout != PageLayout.Single) layout = PageLayout.Single;
        Layout = layout;
        _nav.Layout = layout;
        await ShowCurrentAsync();
    }

    /// <summary>Sposta la coppia di una pagina (F12), per riallineare le doppie pagine.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ShiftPage(string deltaText)
    {
        int delta = int.Parse(deltaText);
        if (_source is null || Layout == PageLayout.Single || !await ConfirmDiscardEditsAsync()) return;
        if (_nav.Shift(delta)) await ShowCurrentAsync();
    }

    /// <summary>F5: rilegge la cartella e ricarica l'immagine dal disco, scartando le modifiche.</summary>
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

    // ---- Presentazione ----

    [RelayCommand]
    private void StartSlideshow(string secondsText)
    {
        double seconds = double.Parse(secondsText, System.Globalization.CultureInfo.InvariantCulture);
        if (!HasImage || _source is null) return;
        if (seconds <= 0)
        {
            string? text = _view.AskText("Presentazione", "Secondi tra un'immagine e l'altra:",
                Settings.SlideshowSeconds.ToString("0.#"),
                s => double.TryParse(s, out double v) && v >= 0.5 && v <= 3600 ? null : "Inserisci un numero tra 0,5 e 3600.");
            if (text is null) return;
            seconds = double.Parse(text);
            Settings.SlideshowSeconds = seconds;
        }
        _slideshowTimer.Interval = TimeSpan.FromSeconds(seconds);
        _slideshowTimer.Start();
        IsSlideshowRunning = true;
        if (!_view.IsFullscreen) _view.SetFullscreen(true);
        Toast($"Presentazione ogni {seconds:0.#} s — Esc o clic per fermare");
    }

    public void StopSlideshow()
    {
        if (!IsSlideshowRunning) return;
        _slideshowTimer.Stop();
        IsSlideshowRunning = false;
    }

    private async void SlideshowTick()
    {
        // Durante una modifica o con un dialogo aperto la presentazione aspetta.
        if (IsModified || IsBusy || Application.Current.Windows.OfType<Window>().Any(w => w.IsActive && w != Application.Current.MainWindow))
            return;
        await HandleNavigation(_nav.Next());
    }

    // ---- Modifica ----

    private void UpdateEditFlags()
    {
        CanUndo = _edit?.CanUndo ?? false;
        CanRedo = _edit?.CanRedo ?? false;
        IsModified = _edit?.IsModified ?? false;
    }

    /// <summary>Crea (se serve) la sessione di modifica sull'immagine visibile. Le coppie vengono unite.</summary>
    private EditSession? EnsureEditSession()
    {
        if (_edit is not null) return _edit;
        if (_visible is null || _visible.Length == 0 || IsLoading) return null;
        if (_visible.Length == 1)
        {
            _edit = new EditSession(_visible[0].Bitmap);
            if (_visible[0].Animation is not null)
                Toast("Le modifiche valgono per il primo fotogramma: l'animazione non verrà salvata");
        }
        else
        {
            var composed = ImageOps.Compose(_visible.Select(v => v.Bitmap).ToList(), Colors.White);
            _edit = new EditSession(composed);
            _detached = true;
            _detachedName = "Pagine unite";
            Toast("Le due pagine sono state unite in un'unica immagine");
        }
        return _edit;
    }

    private async Task ApplyEditAsync(Func<BitmapSource, BitmapSource> operation, bool needsSta = false)
    {
        if (IsBusy) return;
        var session = EnsureEditSession();
        if (session is null) return;
        var source = session.Current;
        IsBusy = true;
        try
        {
            var result = needsSta ? await StaTask.Run(() => operation(source)) : await Task.Run(() => operation(source));
            if (!ReferenceEquals(session, _edit)) return;
            session.Apply(result);
            bool sameSize = result.PixelWidth == source.PixelWidth && result.PixelHeight == source.PixelHeight;
            _view.ShowPages([session.Current], preserveView: sameSize);
            UpdateEditFlags();
            UpdateInfo();
        }
        catch (Exception ex)
        {
            _view.ShowError($"Operazione non riuscita.\n{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Undo()
    {
        if (_edit is null || !_edit.Undo()) return;
        ShowEditCurrent();
    }

    [RelayCommand]
    private void Redo()
    {
        if (_edit is null || !_edit.Redo()) return;
        ShowEditCurrent();
    }

    /// <summary>Mostra lo stato di modifica corrente; tornati all'originale di un'animazione, la riavvia.</summary>
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
        var current = EnsureEditSession()?.Current;
        if (current is null) return;
        int w, h;
        if (percent > 0)
        {
            w = Math.Max(1, current.PixelWidth * percent / 100);
            h = Math.Max(1, current.PixelHeight * percent / 100);
        }
        else
        {
            var size = _view.ShowResizeDialog(current.PixelWidth, current.PixelHeight);
            if (size is null) return;
            (w, h) = size.Value;
        }
        await ApplyEditAsync(b => ImageOps.Resize(b, w, h));
    }

    [RelayCommand]
    private void ToggleRedEyeTool()
    {
        if (!HasImage) return;
        Tool = Tool == ViewerTool.RedEye ? ViewerTool.None : ViewerTool.RedEye;
        Toast(Tool == ViewerTool.RedEye
            ? "Occhi rossi: trascina un rettangolo attorno a ogni occhio (R o Esc per uscire)"
            : "Correzione occhi rossi disattivata");
    }

    private Int32Rect? _lastRectangle;

    /// <summary>Selezioni fatte col mouse nel visualizzatore.</summary>
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

    /// <summary>Tab: riempie l'ultimo rettangolo disegnato (utile per oscurare dati negli screenshot).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task FillLastRectangle()
    {
        if (_lastRectangle is not { } rect || _edit is null) return;
        var color = ParseColor(Settings.RectangleColor, Colors.Red);
        await ApplyEditAsync(b => ImageOps.DrawRectangle(b, rect, color, 0, fill: true), needsSta: true);
    }

    public Task OnTextCommittedAsync(TextPlacement text) => ApplyEditAsync(b => ImageOps.DrawText(b, text), needsSta: true);

    /// <summary>Apre il dialogo carattere/colore e salva la scelta come predefinita.</summary>
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

    // ---- Effetti con anteprima ----

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Effect(string name)
    {
        var session = EnsureEditSession();
        if (session is null) return;
        EffectDefinition effect = name switch
        {
            "BrightnessContrast" => new("Luminosità e contrasto",
                [new("Luminosità", -100, 100, 0), new("Contrasto", -100, 100, 0)],
                (b, v, _) => ImageOps.BrightnessContrast(b, v[0], v[1])),
            "Sharpen" => new("Nitidezza",
                [new("Intensità", 0, 10, 1.5, 0.1)],
                (b, v, s) => ImageOps.Sharpen(b, v[0] * s)),
            "Blur" => new("Sfocatura",
                [new("Raggio", 0, 40, 3, 0.5, " px")],
                (b, v, s) => ImageOps.Blur(b, v[0] * s)),
            "HueSaturation" => new("Tinta e saturazione",
                [new("Tinta", -180, 180, 0, 1, "°"), new("Saturazione", -100, 100, 0)],
                (b, v, _) => ImageOps.HueSaturation(b, v[0], v[1])),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        var values = _view.ShowEffectDialog(effect, session.Current);
        if (values is null) return;
        await ApplyEditAsync(b => effect.Apply(b, values, 1.0));
    }

    // ---- Appunti ----

    [RelayCommand]
    private void Copy()
    {
        var bmp = CurrentBitmapForExport();
        if (bmp is null) return;
        try
        {
            Clipboard.SetImage(bmp);
            Toast("Immagine copiata negli appunti");
        }
        catch (Exception ex)
        {
            _view.ShowError($"Impossibile copiare negli appunti.\n{ex.Message}");
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
                Toast("Gli appunti non contengono immagini");
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
            _detachedName = "Immagine dagli appunti";
            HasImage = true;
            _view.ShowPages([bitmap], preserveView: false);
            UpdateEditFlags();
            UpdateInfo();
        }
        catch (Exception ex)
        {
            _view.ShowError($"Impossibile leggere gli appunti.\n{ex.Message}");
        }
    }

    /// <summary>L'immagine così come la si vede (con modifiche, o le due pagine unite).</summary>
    private BitmapSource? CurrentBitmapForExport()
    {
        if (_edit is not null) return _edit.Current;
        if (_visible is null || _visible.Length == 0 || IsLoading) return null;
        return _visible.Length == 1 ? _visible[0].Bitmap : ImageOps.Compose(_visible.Select(v => v.Bitmap).ToList(), Colors.White);
    }

    // ---- Salvataggio ----

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Save() => await SaveCoreAsync();

    /// <returns>false se l'utente ha annullato o il salvataggio è fallito.</returns>
    private async Task<bool> SaveCoreAsync()
    {
        if (!HasImage) return false;
        string? path = CurrentFilePath;
        if (_detached || path is null || !IsSingleFilePage || !ImageFormats.CanSave(path)) return await SaveAsCoreAsync();
        if (_edit is null || !_edit.IsModified)
        {
            Toast("Nessuna modifica da salvare");
            return true;
        }
        if (Settings.ConfirmOverwrite)
        {
            var answer = _view.Ask($"Sovrascrivere «{Path.GetFileName(path)}» con le modifiche?\n\n«No» per salvare con un altro nome.",
                "Salva", MessageBoxButton.YesNoCancel);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.No) return await SaveAsCoreAsync();
        }
        bool ok = await WriteAsync(_edit.Current, path);
        if (ok) _cache?.Invalidate(_nav.Position);
        return ok;
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
            _view.ShowError("Formato non supportato per il salvataggio: usa jpg, png, webp, bmp, gif, tif o jxr.");
            return false;
        }
        bool ok = await WriteAsync(bitmap, path);
        if (ok && _edit is null && _visible?.Any(v => v.Animation is not null) == true)
            Toast($"Salvato: {Path.GetFileName(path)} (solo il primo fotogramma dell'animazione)");
        if (ok && _source is FolderSource folder &&
            string.Equals(Path.GetDirectoryName(path), folder.Location, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(path, CurrentFilePath, StringComparison.OrdinalIgnoreCase)) _cache?.Invalidate(_nav.Position);
            RefreshFolderKeepingPosition();
        }
        return ok;
    }

    private async Task<bool> WriteAsync(BitmapSource bitmap, string path)
    {
        var meta = !_detached && _visible is { Length: 1 } ? _visible[0].JpegMetadata : null;
        int quality = Settings.JpegQuality;
        IsBusy = true;
        try
        {
            await StaTask.Run(() => { ImageSaver.Save(bitmap, path, quality, meta); return true; });
            _edit?.MarkSaved();
            UpdateEditFlags();
            UpdateInfo();
            Toast($"Salvato: {Path.GetFileName(path)}");
            return true;
        }
        catch (Exception ex)
        {
            _view.ShowError($"Salvataggio non riuscito.\n{ex.Message}");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Chiede cosa fare delle modifiche non salvate. false = l'utente ha annullato.</summary>
    public async Task<bool> ConfirmDiscardEditsAsync()
    {
        if (_edit is null || !_edit.IsModified || !Settings.ConfirmDiscardEdits) return true;
        var answer = _view.Ask("L'immagine ha modifiche non salvate. Salvarle?", "Modifiche non salvate",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return answer switch
        {
            MessageBoxResult.Cancel => false,
            MessageBoxResult.Yes => await SaveCoreAsync(),
            _ => true,
        };
    }

    // ---- Operazioni sui file ----

    private void RefreshFolderKeepingPosition()
    {
        if (_source is not FolderSource folder || _cache is null) return;
        string? current = CurrentFilePath;
        var fresh = FolderSource.Open(folder.Location);
        // Stesso elenco: non serve ricostruire la cache.
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
        string? path = CurrentFilePath;
        if (!IsSingleFilePage || path is null)
        {
            _view.ShowError("Si può eliminare solo un file immagine visualizzato da solo (non una coppia di pagine, un archivio o un'immagine dagli appunti).");
            return;
        }
        if (Settings.ConfirmDelete)
        {
            string where = Settings.DeleteToRecycleBin ? "Spostare nel Cestino" : "Eliminare definitivamente";
            if (_view.Ask($"{where} «{Path.GetFileName(path)}»?", "Elimina", MessageBoxButton.YesNo,
                    Settings.DeleteToRecycleBin ? MessageBoxImage.Question : MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }
        try
        {
            Shell.DeleteFile(path, Settings.DeleteToRecycleBin);
        }
        catch (Exception ex)
        {
            _view.ShowError($"Impossibile eliminare il file.\n{ex.Message}");
            return;
        }
        int position = _nav.Position;
        var fresh = FolderSource.Open(_source!.Location);
        await SetSourceAsync(fresh, Math.Min(position, Math.Max(0, fresh.Pages.Count - 1)), null);
        Toast(Settings.DeleteToRecycleBin ? "Spostato nel Cestino" : "File eliminato");
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Rename()
    {
        string? path = CurrentFilePath;
        if (!IsSingleFilePage || path is null) return;
        if (!await ConfirmDiscardEditsAsync()) return;
        string dir = Path.GetDirectoryName(path)!;
        string ext = Path.GetExtension(path);
        string? name = _view.AskText("Rinomina", "Nuovo nome:", Path.GetFileNameWithoutExtension(path), s =>
        {
            s = s.Trim();
            if (s.Length == 0 || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Nome non valido.";
            string target = Path.Combine(dir, s + ext);
            if (File.Exists(target) && !string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
                return "Esiste già un file con questo nome.";
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
            _view.ShowError($"Impossibile rinominare il file.\n{ex.Message}");
            return;
        }
        var fresh = FolderSource.Open(dir);
        await SetSourceAsync(fresh, Math.Max(0, fresh.IndexOf(newPath)), null);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task BatchRename()
    {
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
            _view.ShowError($"Rinomina non riuscita.\n{ex.Message}");
            renamed = [];
        }
        var fresh = FolderSource.Open(folder.Location);
        int index = current is not null && renamed.TryGetValue(current, out var np) ? fresh.IndexOf(np) : _nav.Position;
        await SetSourceAsync(fresh, Math.Max(0, index), null);
        if (renamed.Count > 0) Toast($"Rinominati {renamed.Count} file");
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
            Toast("Sfondo del desktop impostato");
        }
        catch (Exception ex)
        {
            _view.ShowError($"Impossibile impostare lo sfondo.\n{ex.Message}");
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
                Toast("Nessun metadato trovato");
                return;
            }
            _view.ShowMetadata(Path.GetFileName(_source!.Pages[index].Name), groups);
        }
        catch (Exception ex)
        {
            _view.ShowError($"Impossibile leggere i metadati.\n{ex.Message}");
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenSettings()
    {
        bool autoOrient = Settings.AutoRotateExif;
        if (!_view.ShowSettingsDialog(Settings)) return;
        SettingsStore.Save(Settings);
        ViewMode = Settings.ViewMode;
        // Cambiata la rotazione automatica: le immagini in cache vanno ridecodificate.
        if (autoOrient != Settings.AutoRotateExif && _source is not null && !_detached)
        {
            _cache?.Dispose();
            _cache = new PageCache(_source, Settings.AutoRotateExif);
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
    private void ChangeViewMode(ViewMode mode) => ViewMode = mode;

    /// <summary>A: alterna dimensioni reali e la modalità di vista preferita.</summary>
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
