using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using pViewer.Controls;
using pViewer.Services;

namespace pViewer;

/// <summary>
/// The control bar: buttons over the image for those who browse with the mouse (also in full
/// screen, where the toolbar is hidden). It appears when the pointer moves near the bottom edge
/// of the image and fades out a moment after the pointer leaves it.
/// </summary>
public partial class MainWindow
{
    /// <summary>How close to the bottom edge (in DIPs) the pointer has to move to bring the bar up.</summary>
    private const double BarZone = 130;

    private static readonly TimeSpan BarLinger = TimeSpan.FromSeconds(1.5);

    private readonly DispatcherTimer _barTimer = new() { Interval = BarLinger };
    private bool _barShown;
    private Point _lastPointer = new(double.NaN, double.NaN);

    private void SetUpControlBar()
    {
        ImageArea.PreviewMouseMove += ImageArea_PreviewMouseMove;
        PreviewMouseDown += Window_PreviewMouseDown;
        _barTimer.Tick += (_, _) =>
        {
            // Still in use: the pointer is on it.
            if (ControlBar.IsMouseOver) return;
            HideControlBar();
        };
        ControlBar.SizeChanged += (_, _) => PlaceToast();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.MainViewModel.HasImage) && !_vm.HasImage) HideControlBar();
        };
        Closed += (_, _) => _barTimer.Stop();
        // The wheel over the bar does what it does over the image (zoom, or scroll in fit width).
        ControlBar.MouseWheel += (_, e) =>
        {
            Viewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent });
            e.Handled = true;
        };
        // Ctrl, Alt and Shift start crop, rectangle and text: the bar must not be in the way of a
        // selection that starts at the bottom of the image.
        PreviewKeyDown += (_, e) =>
        {
            if ((e.Key == Key.System ? e.SystemKey : e.Key) is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
                HideControlBar();
        };
    }

    /// <summary>
    /// The back and forward buttons on the side of many mice: previous and next image. Handled here,
    /// so Windows does not also turn them into a "browser back/forward" command.
    /// </summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2)) return;
        e.Handled = true;
        // While typing on the image the keys do nothing either (see Window_PreviewKeyDown).
        if (Viewer.IsEditingText || HasModalDialog) return;
        _vm.StopSlideshow();
        if (e.ChangedButton == MouseButton.XButton1) _vm.PreviousCommand.Execute(null);
        else _vm.NextCommand.Execute(null);
    }

    /// <summary>A modifier held (crop, rectangle, text), the red-eye tool or text being typed on the image.</summary>
    private bool EditingOnImage =>
        Keyboard.Modifiers != ModifierKeys.None || _vm.Tool != ViewerTool.None || Viewer.IsEditingText;

    private void ImageArea_PreviewMouseMove(object sender, MouseEventArgs e) =>
        PointerMoved(PointToScreen(e.GetPosition(this)), e.GetPosition(ImageArea).Y,
            e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed);

    /// <param name="screen">Where the pointer is on the screen.</param>
    /// <param name="y">Its height inside the image area.</param>
    /// <param name="buttonDown">A mouse button is held.</param>
    internal void PointerMoved(Point screen, double y, bool buttonDown)
    {
        if (!ControlBarAllowed || !_vm.HasImage) return;
        // A pan or a selection being dragged: the bar would only get in the way.
        if (buttonDown) return;
        // WPF also raises MouseMove when what lies under a still pointer changes (a new image in
        // the slideshow): only a real movement counts.
        if (screen == _lastPointer) return;
        _lastPointer = screen;

        if (EditingOnImage) { HideControlBar(); return; }
        // In a small window the zone stays in the lower third of the image.
        if (y < ImageArea.ActualHeight - Math.Min(BarZone, ImageArea.ActualHeight / 3) && !ControlBar.IsMouseOver) return;
        ShowControlBar();
        _barTimer.Stop();
        _barTimer.Start();
    }

    private void ShowControlBar()
    {
        if (_barShown) return;
        _barShown = true;
        HotkeyText.Apply(ControlBar, _settings.Hotkeys);
        ControlBar.Visibility = Visibility.Visible;
        ControlBar.IsHitTestVisible = true;
        ControlBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        PlaceToast();
    }

    private void HideControlBar()
    {
        _barTimer.Stop();
        if (!_barShown) return;
        _barShown = false;
        // No clicks on a bar that is fading out.
        ControlBar.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) =>
        {
            if (!_barShown) ControlBar.Visibility = Visibility.Collapsed;
        };
        ControlBar.BeginAnimation(OpacityProperty, fade);
        PlaceToast();
    }

    /// <summary>Messages appear above the bar while it is up, instead of under it.</summary>
    private void PlaceToast() =>
        ToastBox.Margin = new Thickness(0, 0, 0, _barShown ? ControlBar.Margin.Bottom + ControlBar.ActualHeight + 12 : 28);

    /// <summary>The bar's full screen button shows what it will do: enter or leave full screen.</summary>
    private void UpdateBarFullscreenButton()
    {
        BarFullscreenButton.Content = _fullscreen ? "\xE73F" : "\xE740"; // BackToWindow, FullScreen
        HotkeyText.SetLabel(BarFullscreenButton, _fullscreen ? "Leave full screen" : "Full screen");
        HotkeyText.Apply(BarFullscreenButton, _settings.Hotkeys);
    }

    /// <summary>Settings › Appearance: in the window and in full screen, only in full screen, or never.</summary>
    private bool ControlBarAllowed => _settings.ControlBar switch
    {
        ControlBarMode.Always => true,
        ControlBarMode.FullScreenOnly => _fullscreen,
        _ => false,
    };

    /// <summary>After a change of the setting, and on entering or leaving full screen.</summary>
    private void ApplyControlBarSetting()
    {
        if (!ControlBarAllowed) HideControlBar();
        else if (_barShown) HotkeyText.Apply(ControlBar, _settings.Hotkeys);
    }

    private void ResetView_Click(object sender, RoutedEventArgs e) => Viewer.ResetView();

    private void BarSlideshow_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsSlideshowRunning) _vm.StopSlideshow();
        else SlideshowButton_Click(sender, e);
    }
}
