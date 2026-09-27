using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using pViewer.Controls;
using pViewer.Core;
using pViewer.Core.Sources;
using pViewer.Imaging;
using pViewer.Services;
using pViewer.ViewModels;
using pViewer.Views;

namespace pViewer;

public partial class MainWindow : Window, IMainView
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly ContextMenu _mainMenu;

    private bool _fullscreen;
    private WindowState _restoreState;
    private bool _closeConfirmed;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        // Dimensione iniziale proporzionata allo schermo (a 150% di scala 1180×780 non ci sta).
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, workArea.Width * 0.85);
        Height = Math.Min(Height, workArea.Height * 0.85);
        _vm = new MainViewModel(this, settings);
        DataContext = _vm;

        _mainMenu = (ContextMenu)Resources["MainMenu"];
        Viewer.ContextMenu = _mainMenu;
        _mainMenu.Opened += (_, _) =>
        {
            // I nomi dentro una risorsa non generano campi: si cercano nell'albero logico del menu.
            if (LogicalTreeHelper.FindLogicalNode(_mainMenu, "ToolbarMenuItem") is MenuItem toolbar)
                toolbar.IsChecked = _settings.ShowToolbar;
            if (LogicalTreeHelper.FindLogicalNode(_mainMenu, "StatusBarMenuItem") is MenuItem status)
                status.IsChecked = _settings.ShowStatusBar;
        };

        Viewer.SelectionCompleted += async (_, e) => await _vm.OnSelectionAsync(e.Kind, e.Rect);
        Viewer.TextRequested += (_, p) =>
        {
            if (_vm.CanEdit) Viewer.BeginTextEdit(p, _vm.CurrentTextStyle);
        };
        Viewer.TextCommitted += async (_, t) => await _vm.OnTextCommittedAsync(t);
        Viewer.TextStyleRequested += (_, _) =>
        {
            var style = _vm.ChooseTextStyle();
            if (style is not null) Viewer.UpdateTextStyle(style);
        };
        Viewer.DoubleClicked += (_, _) =>
        {
            if (_vm.HasImage) SetFullscreen(!_fullscreen);
            else _vm.OpenCommand.Execute(null);
        };
        Viewer.UserClicked += (_, _) => _vm.StopSlideshow();
        Viewer.ZoomChanged += (_, _) => _vm.SetZoom(Viewer.Zoom);

        _vm.PropertyChanged += Vm_PropertyChanged;
        PreviewKeyDown += Window_PreviewKeyDown;
        DragOver += Window_DragOver;
        Drop += Window_Drop;
        SourceInitialized += (_, _) => RestorePlacement();
        Closing += Window_Closing;

        ApplySettingsToView();
    }

    public Task OpenAsync(string path) => _vm.OpenPathAsync(path);

    // ---- Stato e impostazioni ----

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Tool):
                Viewer.Tool = _vm.Tool;
                break;
            case nameof(MainViewModel.WhiteBackground):
                UpdateBackground();
                break;
            case nameof(MainViewModel.IsLoading):
            case nameof(MainViewModel.IsBusy):
                Progress.Visibility = _vm.IsLoading || _vm.IsBusy ? Visibility.Visible : Visibility.Collapsed;
                Viewer.Cursor = _vm.IsBusy ? Cursors.Wait : null;
                break;
            case nameof(MainViewModel.IsSlideshowRunning):
                Viewer.Cursor = _vm.IsSlideshowRunning ? Cursors.None : null;
                break;
        }
    }

    private void ApplySettingsToView()
    {
        Viewer.CropColor = MainViewModel.ParseColor(_settings.CropColor, Colors.Red);
        Viewer.RectangleColor = MainViewModel.ParseColor(_settings.RectangleColor, Colors.Red);
        Viewer.RectangleThickness = Math.Max(1, _settings.RectangleThickness);
        Viewer.PixelatedZoom = _settings.PixelatedZoom;
        UpdateBars();
        UpdateBackground();
    }

    private void UpdateBars()
    {
        Toolbar.Visibility = !_fullscreen && _settings.ShowToolbar ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.Visibility = !_fullscreen && _settings.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBackground()
    {
        Color color = _vm.WhiteBackground ? Colors.White
            : _fullscreen ? Colors.Black
            : IsLightTheme() ? Color.FromRgb(0xEB, 0xEB, 0xEB) : Color.FromRgb(0x1C, 0x1C, 0x1C);
        Viewer.Background = new SolidColorBrush(color);
    }

    private bool IsLightTheme() => _settings.Theme switch
    {
        AppTheme.Light => true,
        AppTheme.Dark => false,
        _ => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                               "AppsUseLightTheme", 1) is int v && v != 0,
    };

    // ---- Tastiera ----

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Mentre si scrive (testo sull'immagine) i tasti vanno alla casella.
        if (Keyboard.FocusedElement is TextBox) return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys mods = Keyboard.Modifiers;
        bool none = mods == ModifierKeys.None, ctrl = mods == ModifierKeys.Control, alt = mods == ModifierKeys.Alt;
        bool shift = mods == ModifierKeys.Shift;
        e.Handled = true;

        switch (key)
        {
            case Key.Right or Key.Space or Key.E or Key.PageUp when none:
            case Key.Right when alt:
                _vm.NextCommand.Execute(null); break;
            case Key.Left or Key.Q or Key.PageDown when none:
            case Key.Left when alt:
                _vm.PreviousCommand.Execute(null); break;
            case Key.Home when none: _vm.FirstCommand.Execute(null); break;
            case Key.End when none: _vm.LastCommand.Execute(null); break;

            case Key.W or Key.Q when ctrl: Close(); break;
            case Key.Escape when none: HandleEscape(); break;

            case Key.S when ctrl: _vm.SaveCommand.Execute(null); break;
            case Key.S when mods == (ModifierKeys.Control | ModifierKeys.Shift) || mods == (ModifierKeys.Control | ModifierKeys.Alt):
                _vm.SaveAsCommand.Execute(null); break;
            case Key.O when ctrl: _vm.OpenCommand.Execute(null); break;
            case Key.F2 when none: _vm.RenameCommand.Execute(null); break;
            case Key.Delete when none: _vm.DeleteCommand.Execute(null); break;
            case Key.F5 when none: _vm.ReloadCommand.Execute(null); break;

            case Key.Up when none: _vm.RotateCommand.Execute("90"); break;
            case Key.Down when none: _vm.RotateCommand.Execute("-90"); break;
            case Key.Up when alt: _vm.FlipCommand.Execute("H"); break;
            case Key.Down when alt: _vm.FlipCommand.Execute("V"); break;

            case Key.OemPlus or Key.Add when none || ctrl: Viewer.ZoomIn(); break;
            case Key.OemMinus or Key.Subtract when none || ctrl: Viewer.ZoomOut(); break;
            case Key.D0 or Key.NumPad0 when ctrl: Viewer.ResetView(); break;
            case Key.D1 or Key.NumPad1 when ctrl: Viewer.SetZoom(1); break;
            case Key.NumPad4 when none: Viewer.Pan(50, 0); break;
            case Key.NumPad6 when none: Viewer.Pan(-50, 0); break;
            case Key.NumPad8 when none: Viewer.Pan(0, 50); break;
            case Key.NumPad2 when none: Viewer.Pan(0, -50); break;
            case Key.NumPad5 when none: Viewer.ResetView(); break;
            case Key.A when none: _vm.ToggleActualSizeCommand.Execute(null); break;

            case Key.M when none: _vm.SetLayoutCommand.Execute(PageLayout.Manga); break;
            case Key.C when none: _vm.SetLayoutCommand.Execute(PageLayout.Comic); break;
            case Key.F12 when none: _vm.ShiftPageCommand.Execute("1"); break;
            case Key.F12 when shift: _vm.ShiftPageCommand.Execute("-1"); break;

            case Key.F11 when none: SetFullscreen(!_fullscreen); break;
            case Key.Enter when alt:
                if (_fullscreen) SetFullscreen(false);
                else WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                break;
            case Key.W when none: _vm.ToggleWhiteBackgroundCommand.Execute(null); break;
            case Key.T when none: ToggleToolbar_Click(this, new RoutedEventArgs()); break;

            case Key.Z when ctrl: _vm.UndoCommand.Execute(null); break;
            case Key.Y when ctrl:
            case Key.Z when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                _vm.RedoCommand.Execute(null); break;
            case Key.C when ctrl: _vm.CopyCommand.Execute(null); break;
            case Key.V when ctrl: _vm.PasteCommand.Execute(null); break;
            case Key.I when ctrl: _vm.InvertCommand.Execute(null); break;
            case Key.G when ctrl: _vm.GrayscaleCommand.Execute(null); break;
            case Key.R when ctrl: _vm.ResizeCommand.Execute("0"); break;
            case Key.R when none: _vm.ToggleRedEyeToolCommand.Execute(null); break;
            case Key.Tab when none: _vm.FillLastRectangleCommand.Execute(null); break;
            case Key.I when none: _vm.ShowMetadataCommand.Execute(null); break;
            case Key.P when none: Viewer.ToggleAnimationPause(); break;
            case Key.F1 when none: Help_Click(this, new RoutedEventArgs()); break;

            default: e.Handled = false; break;
        }
    }

    private void HandleEscape()
    {
        if (_vm.IsSlideshowRunning) { _vm.StopSlideshow(); if (_fullscreen) SetFullscreen(false); }
        else if (_vm.Tool != ViewerTool.None) _vm.ToggleRedEyeToolCommand.Execute(null);
        else if (_fullscreen) SetFullscreen(false);
        else Close();
    }

    // ---- Schermo intero ----

    public bool IsFullscreen => _fullscreen;

    public void SetFullscreen(bool fullscreen)
    {
        if (fullscreen == _fullscreen) return;
        _fullscreen = fullscreen;
        if (fullscreen)
        {
            _restoreState = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // Passando da Normal a Maximized senza bordi la finestra copre anche la barra delle applicazioni.
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _restoreState == WindowState.Minimized ? WindowState.Normal : _restoreState;
            _vm.StopSlideshow();
        }
        UpdateBars();
        UpdateBackground();
    }

    public IntPtr WindowHandle => new WindowInteropHelper(this).Handle;

    // ---- Posizione della finestra ----

    private void RestorePlacement()
    {
        var p = _settings.Window;
        if (p is null || p.Width < 200 || p.Height < 150) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var rect = new Rect(p.Left, p.Top, p.Width, p.Height);
        if (!screen.IntersectsWith(rect)) return; // monitor scollegato: resta la posizione predefinita
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = p.Left;
        Top = p.Top;
        Width = p.Width;
        Height = p.Height;
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        if (_fullscreen) SetFullscreen(false);
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.Window = new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_closeConfirmed && _vm.IsModified && _settings.ConfirmDiscardEdits)
        {
            e.Cancel = true;
            if (!await _vm.ConfirmDiscardEditsAsync()) return;
            _closeConfirmed = true;
            // Close() dentro Closing verrebbe ignorato: lo si richiama a evento concluso.
            _ = Dispatcher.BeginInvoke(Close);
            return;
        }
        SavePlacement();
        SettingsStore.Save(_settings);
        _vm.Dispose();
    }

    // ---- Trascina e rilascia ----

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            Activate();
            await _vm.OpenPathAsync(files[0]);
        }
    }

    // ---- Pulsanti e menu ----

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Viewer.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Viewer.ZoomOut();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenMenuBelow(FrameworkElement target, ContextMenu menu)
    {
        menu.DataContext = DataContext;
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ViewModeButton_Click(object sender, RoutedEventArgs e) =>
        OpenMenuBelow((FrameworkElement)sender, (ContextMenu)Resources["ViewModeMenu"]);

    private void LayoutButton_Click(object sender, RoutedEventArgs e) =>
        OpenMenuBelow((FrameworkElement)sender, (ContextMenu)Resources["LayoutMenu"]);

    private void MoreButton_Click(object sender, RoutedEventArgs e) => OpenMenuBelow((FrameworkElement)sender, _mainMenu);

    private void SlideshowButton_Click(object sender, RoutedEventArgs e) =>
        _vm.StartSlideshowCommand.Execute(_settings.SlideshowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void ToggleToolbar_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowToolbar = !_settings.ShowToolbar;
        UpdateBars();
    }

    private void ToggleStatusBar_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowStatusBar = !_settings.ShowStatusBar;
        UpdateBars();
    }

    private void TextStyle_Click(object sender, RoutedEventArgs e)
    {
        var style = _vm.ChooseTextStyle();
        if (style is not null && Viewer.IsEditingText) Viewer.UpdateTextStyle(style);
    }

    private void Help_Click(object sender, RoutedEventArgs e) => new HelpWindow { Owner = this }.ShowDialog();
    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    // ---- IMainView ----

    public void ShowPages(IReadOnlyList<BitmapSource> pages, bool preserveView, IReadOnlyList<ImageAnimation?>? animations = null) =>
        Viewer.SetPages(pages, preserveView, null, animations);

    public void ShowPreview(BitmapSource thumbnail, int fullWidth, int fullHeight) =>
        Viewer.SetPages([thumbnail], false, new Size(fullWidth, fullHeight));

    public void ClearPages() => Viewer.Clear();

    public MessageBoxResult Ask(string message, string title, MessageBoxButton buttons, MessageBoxImage icon = MessageBoxImage.Question) =>
        MessageBox.Show(this, message, title, buttons, icon);

    public void ShowError(string message) =>
        MessageBox.Show(this, message, "pViewer", MessageBoxButton.OK, MessageBoxImage.Warning);

    public string? PickOpenFile(string? initialDirectory)
    {
        var dlg = new OpenFileDialog { Title = "Apri", Filter = ImageFormats.OpenDialogFilter };
        if (initialDirectory is not null && Directory.Exists(initialDirectory)) dlg.InitialDirectory = initialDirectory;
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    public string? PickSaveFile(string? initialDirectory, string fileName)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Salva con nome",
            Filter = ImageFormats.SaveDialogFilter,
            FileName = Path.GetFileNameWithoutExtension(fileName),
            OverwritePrompt = true,
        };
        // Preseleziona il filtro corrispondente all'estensione originale.
        dlg.FilterIndex = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => 2, ".webp" => 3, ".bmp" => 4, ".gif" => 5, ".tif" or ".tiff" => 6, ".jxr" or ".wdp" => 7, _ => 1,
        };
        if (initialDirectory is not null && Directory.Exists(initialDirectory)) dlg.InitialDirectory = initialDirectory;
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    public string? AskText(string title, string prompt, string initial, Func<string, string?>? validate = null)
    {
        var dlg = new InputDialog(title, prompt, initial, validate) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }

    public double[]? ShowEffectDialog(EffectDefinition effect, BitmapSource source)
    {
        var dlg = new EffectDialog(effect, source) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.Values : null;
    }

    public (int Width, int Height)? ShowResizeDialog(int width, int height)
    {
        var dlg = new ResizeDialog(width, height) { Owner = this };
        return dlg.ShowDialog() == true ? (dlg.ResultWidth, dlg.ResultHeight) : null;
    }

    public void ShowBatchDialog(IImageSource source, AppSettings settings) =>
        new BatchDialog(source, settings) { Owner = this }.ShowDialog();

    public BatchRenameOptions? ShowBatchRenameDialog(string suggestedBase, int count)
    {
        var dlg = new BatchRenameDialog(suggestedBase, count) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.Options : null;
    }

    public bool ShowSettingsDialog(AppSettings settings)
    {
        var dlg = new SettingsWindow(settings) { Owner = this };
        if (dlg.ShowDialog() != true) return false;
        App.ApplyTheme(settings.Theme);
        ApplySettingsToView();
        return true;
    }

    public void ShowMetadata(string title, IReadOnlyList<MetadataGroup> groups) =>
        new MetadataWindow(title, groups) { Owner = this }.ShowDialog();

    public TextStyle? ShowTextStyleDialog(TextStyle current)
    {
        var dlg = new TextStyleDialog(current) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }
}
