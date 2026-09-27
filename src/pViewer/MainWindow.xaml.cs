using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
    private bool _closeWhenIdle;

    static MainWindow()
    {
        // The Fluent theme draws the check mark only on checkable menu items, and a checkable item
        // flips its own IsChecked when clicked. Re-read the bound value once the click has been
        // handled, so the view model stays the only source of truth (e.g. clicking the mode that is
        // already active, or a command cancelled by the user).
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.ClickEvent, new RoutedEventHandler((sender, _) =>
        {
            var item = (MenuItem)sender;
            if (BindingOperations.GetBindingExpression(item, MenuItem.IsCheckedProperty) is { } binding)
                item.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, binding.UpdateTarget);
        }));
    }

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        // Initial size proportional to the screen (at 150% scaling 1180×780 does not fit).
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, workArea.Width * 0.85);
        Height = Math.Min(Height, workArea.Height * 0.85);
        _vm = new MainViewModel(this, settings);
        DataContext = _vm;

        _mainMenu = (ContextMenu)Resources["MainMenu"];
        Viewer.ContextMenu = _mainMenu;
        _mainMenu.Opened += (_, _) =>
        {
            // Names inside a resource do not generate fields: look them up in the menu's logical tree.
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
        SourceInitialized += (_, _) =>
        {
            RestorePlacement();
            HwndSource.FromHwnd(WindowHandle)?.AddHook(WndProc);
        };
        Closing += Window_Closing;

        ApplySettingsToView();
    }

    public Task OpenAsync(string path) => _vm.OpenPathAsync(path);

    // ---- State and settings ----

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
                UpdateCursor();
                break;
            case nameof(MainViewModel.IsSlideshowRunning):
                UpdateCursor();
                break;
        }
    }

    /// <summary>
    /// Busy and slideshow cursors apply to the image area only (dialogs and menus keep a normal
    /// pointer); the viewer keeps them over its own pan and red-eye cursors.
    /// </summary>
    private void UpdateCursor()
    {
        Viewer.CursorOverride = _vm.IsBusy ? Cursors.Wait : _vm.IsSlideshowRunning ? Cursors.None : null;
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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Windows switched between light and dark: with the "Same as Windows" theme the Fluent
        // chrome follows by itself, the viewer background has to be updated here.
        const int WM_SETTINGCHANGE = 0x001A;
        if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero &&
            System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            UpdateBackground();
        return IntPtr.Zero;
    }

    private bool IsLightTheme() => _settings.Theme switch
    {
        AppTheme.Light => true,
        AppTheme.Dark => false,
        _ => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                               "AppsUseLightTheme", 1) is int v && v != 0,
    };

    // ---- Keyboard ----

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // While typing (text on the image) keys go to the text box.
        if (Keyboard.FocusedElement is TextBox) return;
        // Keys pressed in an open menu tunnel through the window too: leave them to the menu
        // (arrows move through the items, Esc closes the menu and not the app).
        if (e.OriginalSource is DependencyObject source && IsInsideMenu(source)) return;
        // The text box lost focus (e.g. a toolbar click) but the text is still being edited:
        // Esc cancels it, and no other key must act on the image underneath.
        if (Viewer.IsEditingText)
        {
            if (e.Key == Key.Escape) { Viewer.CancelTextEdit(); e.Handled = true; }
            return;
        }

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

    private static bool IsInsideMenu(DependencyObject element)
    {
        for (DependencyObject? d = element; d is not null;
             d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is MenuBase or MenuItem) return true;
        }
        return false;
    }

    private void HandleEscape()
    {
        if (_vm.IsSlideshowRunning) { _vm.StopSlideshow(); if (_fullscreen) SetFullscreen(false); }
        else if (_vm.Tool != ViewerTool.None) _vm.ToggleRedEyeToolCommand.Execute(null);
        else if (_fullscreen) SetFullscreen(false);
        else Close();
    }

    // ---- Full screen ----

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
            // Going from Normal to Maximized without borders the window also covers the taskbar.
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            // Through Normal again: going back to Maximized directly would keep the borderless
            // full-monitor bounds and cover the taskbar.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            if (_restoreState == WindowState.Maximized) WindowState = WindowState.Maximized;
            _vm.StopSlideshow();
        }
        UpdateBars();
        UpdateBackground();
    }

    public IntPtr WindowHandle => new WindowInteropHelper(this).Handle;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    // Modal dialogs, WPF or Win32 (file dialogs), disable their owner window.
    public bool HasModalDialog => WindowHandle != IntPtr.Zero && !IsWindowEnabled(WindowHandle);

    // ---- Window position ----

    private void RestorePlacement()
    {
        var p = _settings.Window;
        if (p is null || p.Width < 200 || p.Height < 150) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var rect = new Rect(p.Left, p.Top, p.Width, p.Height);
        if (!screen.IntersectsWith(rect)) return; // disconnected monitor: keep the default position
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
            // Closed while minimized: remember the state it was minimized from.
            Maximized = WindowState == WindowState.Maximized
                        || (WindowState == WindowState.Minimized && _lastShownState == WindowState.Maximized),
        };
    }

    private WindowState _lastShownState = WindowState.Normal;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Minimized && !_fullscreen) _lastShownState = WindowState;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        // A save or an edit is still running: exiting now would kill it halfway (the StaTask threads
        // are background threads). Wait for it, then close.
        if (_vm.IsBusy)
        {
            e.Cancel = true;
            if (_closeWhenIdle) return; // already waiting
            _closeWhenIdle = true;
            while (_vm.IsBusy) await Task.Delay(100);
            _closeWhenIdle = false;
            // Edits made while a save was running after "Save changes?" must be asked about again.
            _closeConfirmed = false;
            _ = Dispatcher.BeginInvoke(Close);
            return;
        }
        if (!_closeConfirmed && _vm.IsModified && _settings.ConfirmDiscardEdits)
        {
            e.Cancel = true;
            if (!await _vm.ConfirmDiscardEditsAsync()) return;
            _closeConfirmed = true;
            // Close() inside Closing would be ignored: call it again once the event is over.
            _ = Dispatcher.BeginInvoke(Close);
            return;
        }
        SavePlacement();
        SettingsStore.Save(_settings);
        _vm.Dispose();
    }

    // ---- Drag and drop ----

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // async void on purpose: unexpected errors reach the app error handler instead of being lost.
    private async void OpenDropped(string path) => await _vm.OpenPathAsync(path);

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            Activate();
            // Opened after the drop has returned: a "save changes?" prompt inside the OLE drop
            // callback would keep the Explorer window it came from frozen. Only the first item is
            // opened; the others of the same folder are reachable with next/previous.
            string path = files[0];
            _ = Dispatcher.InvokeAsync(() => OpenDropped(path));
        }
    }

    // ---- Buttons and menus ----

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Viewer.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Viewer.ZoomOut();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenMenuBelow(FrameworkElement target, ContextMenu menu)
    {
        menu.DataContext = DataContext;
        // An open menu inherits font properties from its PlacementTarget, and the toolbar buttons
        // use the icon font: without this every item would render as empty boxes.
        menu.FontFamily = FontFamily;
        menu.FontSize = FontSize;
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.Closed += OnClosed;
        menu.IsOpen = true;

        // Give the menu back to the context menu service, otherwise a later right-click on the
        // image would open it under the toolbar button.
        void OnClosed(object sender, RoutedEventArgs e)
        {
            menu.Closed -= OnClosed;
            menu.ClearValue(ContextMenu.PlacementTargetProperty);
            menu.ClearValue(ContextMenu.PlacementProperty);
        }
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

    public void ResetView() => Viewer.ResetView();

    public MessageBoxResult Ask(string message, string title, MessageBoxButton buttons, MessageBoxImage icon = MessageBoxImage.Question) =>
        MessageDialog.Show(this, message, title, buttons, icon);

    public void ShowError(string message) =>
        MessageDialog.Show(this, message, "pViewer", MessageBoxButton.OK, MessageBoxImage.Warning);

    public string? PickOpenFile(string? initialDirectory)
    {
        var dlg = new OpenFileDialog { Title = "Open", Filter = ImageFormats.OpenDialogFilter };
        if (initialDirectory is not null && Directory.Exists(initialDirectory)) dlg.InitialDirectory = initialDirectory;
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    public string? PickSaveFile(string? initialDirectory, string fileName)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save as",
            Filter = ImageFormats.SaveDialogFilter,
            FileName = Path.GetFileNameWithoutExtension(fileName),
            OverwritePrompt = true,
        };
        // Preselect the filter matching the original extension.
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

    public double[]? ShowEffectDialog(EffectDefinition effect, BitmapSource preview, double scale)
    {
        var dlg = new EffectDialog(effect, preview, scale) { Owner = this };
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
