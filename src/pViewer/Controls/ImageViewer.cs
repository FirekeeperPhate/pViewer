using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using pViewer.Imaging;
using pViewer.Services;

namespace pViewer.Controls;

public enum ViewerTool { None, RedEye }

public enum SelectionKind { Crop, Rectangle, RedEye }

public sealed class SelectionEventArgs(SelectionKind kind, Int32Rect rect) : EventArgs
{
    public SelectionKind Kind { get; } = kind;
    public Int32Rect Rect { get; } = rect;
}

public sealed record TextStyle(string FontFamily, double FontSize, bool Bold, bool Italic, Color Color);

/// <summary>
/// Viewer with zoom and pan. Content is expressed in image pixels and is
/// scaled/moved by a (GPU accelerated) transform: the image is never
/// resampled for display. It also handles the editing gestures:
/// Ctrl+drag = crop, Alt+drag = rectangle, Shift+click = text, red-eye tool.
/// </summary>
public sealed class ImageViewer : Border
{
    private const double ZoomStep = 1.25;
    private const double MaxScreenZoom = 40;

    private readonly Canvas _host = new();
    private readonly Canvas _content = new();
    private readonly MatrixTransform _transform = new();
    private readonly Rectangle _selection = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly List<Image> _images = [];

    private IReadOnlyList<BitmapSource> _pages = [];
    private Size _contentSize;
    private double _scale = 1;
    private Vector _offset;
    private bool _userAdjusted;

    // Current drag state.
    private enum DragMode { None, Pan, Select, MoveText }
    private DragMode _drag;
    private SelectionKind _selectionKind;
    private Point _dragStartViewport;
    private Point _dragStartImage;
    private Vector _dragStartOffset;
    private bool _panStarted;
    private const double PanThreshold = 3;
    private Point _textDragStart;

    // Text being edited.
    private Border? _textFrame;
    private TextBox? _textBox;
    private TextStyle? _textStyle;

    public ImageViewer()
    {
        ClipToBounds = true;
        Focusable = false;
        _content.RenderTransform = _transform;
        _content.Children.Add(_selection);
        _host.Children.Add(_content);
        // The window rounds layout to device pixels; inside the zoomed canvas that would resize the
        // pages by a fraction of a pixel at 125/150% (blurry at 100%, grid off from the selections).
        _host.UseLayoutRounding = false;
        Child = _host;
    }

    // ---- Properties ----

    public static readonly DependencyProperty ViewModeProperty = DependencyProperty.Register(
        nameof(ViewMode), typeof(ViewMode), typeof(ImageViewer),
        new PropertyMetadata(ViewMode.ShrinkToFit, (d, _) => ((ImageViewer)d).ResetView()));

    public ViewMode ViewMode
    {
        get => (ViewMode)GetValue(ViewModeProperty);
        set => SetValue(ViewModeProperty, value);
    }

    public static readonly DependencyProperty PixelatedZoomProperty = DependencyProperty.Register(
        nameof(PixelatedZoom), typeof(bool), typeof(ImageViewer),
        new PropertyMetadata(true, (d, _) => ((ImageViewer)d).UpdateScalingMode()));

    /// <summary>Beyond 250% shows sharp pixels instead of smoothed ones.</summary>
    public bool PixelatedZoom
    {
        get => (bool)GetValue(PixelatedZoomProperty);
        set => SetValue(PixelatedZoomProperty, value);
    }

    public ViewerTool Tool { get; set; }

    private Cursor? _cursorOverride;

    /// <summary>Cursor imposed by the window (busy, hidden during the slideshow); null = normal.</summary>
    public Cursor? CursorOverride
    {
        get => _cursorOverride;
        set
        {
            _cursorOverride = value;
            if (_drag == DragMode.None) Cursor = RestingCursor;
        }
    }

    private Cursor? RestingCursor => CursorOverride ?? (Tool == ViewerTool.RedEye ? Cursors.Cross : null);
    public Color CropColor { get; set; } = Colors.Red;
    public Color RectangleColor { get; set; } = Colors.Red;
    public double RectangleThickness { get; set; } = 5;

    public bool HasContent => _pages.Count > 0;
    public bool IsEditingText => _textFrame is not null;

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Screen pixels per image pixel (1 = 100%).</summary>
    public double Zoom => _scale * DpiScale;

    public event EventHandler? ZoomChanged;
    public event EventHandler<SelectionEventArgs>? SelectionCompleted;
    public event EventHandler<Point>? TextRequested;
    public event EventHandler<TextPlacement>? TextCommitted;
    public event EventHandler? TextStyleRequested;
    public event EventHandler? DoubleClicked;
    /// <summary>Any click: used to stop the slideshow.</summary>
    public event EventHandler? UserClicked;

    // ---- Content ----

    /// <param name="preserveView">Keeps zoom and position if the size does not change (e.g. after an edit).</param>
    /// <param name="logicalSize">Size to use instead of the real one (low resolution preview).</param>
    /// <param name="animations">For each page, the frames to animate (null = static image).</param>
    public void SetPages(IReadOnlyList<BitmapSource> pages, bool preserveView = false, Size? logicalSize = null,
                         IReadOnlyList<ImageAnimation?>? animations = null)
    {
        var oldSize = _contentSize;
        _pages = pages;
        StopAnimations();
        // A pause (P) belongs to the image it was pressed on: the next image plays normally.
        if (!preserveView) IsAnimationPaused = false;
        foreach (var img in _images) _content.Children.Remove(img);
        _images.Clear();

        double PageWidth(BitmapSource p) => logicalSize?.Width ?? p.PixelWidth;
        double PageHeight(BitmapSource p) => logicalSize?.Height ?? p.PixelHeight;

        double x = 0, height = pages.Count == 0 ? 0 : pages.Max(PageHeight);
        int insertAt = 0;
        foreach (var page in pages)
        {
            var img = new Image { Source = page, Width = PageWidth(page), Height = PageHeight(page), Stretch = Stretch.Fill };
            Canvas.SetLeft(img, x);
            Canvas.SetTop(img, Math.Floor((height - PageHeight(page)) / 2)); // whole pixels, like the joined image
            _content.Children.Insert(insertAt++, img);
            _images.Add(img);
            if (animations is not null && insertAt - 1 < animations.Count && animations[insertAt - 1] is { } anim)
                StartAnimation(img, anim);
            x += PageWidth(page);
        }
        _contentSize = new Size(x, height);
        _content.Width = x;
        _content.Height = height;

        // Same size (an edit like a rectangle or an effect): keep zoom and position, including the
        // scroll position of "fit width", which is not a manual adjustment.
        if (preserveView && oldSize == _contentSize)
        {
            ClampAndApply();
        }
        else
        {
            CancelTextEdit();
            CancelDrag(); // a selection or pan started on the previous image must not land on this one
            ResetView();
        }
        UpdateScalingMode();
    }

    public void Clear() => SetPages([]);

    // ---- Animations (GIF, WebP, APNG) ----

    private readonly List<(Image Image, AnimationClock Clock)> _animations = [];

    public bool HasAnimation => _animations.Count > 0;
    public bool IsAnimationPaused { get; private set; }

    private void StartAnimation(Image image, ImageAnimation animation)
    {
        var keyFrames = new ObjectAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        var time = TimeSpan.Zero;
        for (int i = 0; i < animation.Frames.Count; i++)
        {
            keyFrames.KeyFrames.Add(new DiscreteObjectKeyFrame(animation.Frames[i], KeyTime.FromTimeSpan(time)));
            time += animation.Delays[i];
        }
        keyFrames.Duration = new Duration(time);
        var clock = keyFrames.CreateClock();
        image.ApplyAnimationClock(Image.SourceProperty, clock);
        if (IsAnimationPaused) clock.Controller?.Pause();
        _animations.Add((image, clock));
    }

    private void StopAnimations()
    {
        foreach (var (image, clock) in _animations)
        {
            clock.Controller?.Stop();
            image.ApplyAnimationClock(Image.SourceProperty, null);
        }
        _animations.Clear();
    }

    /// <summary>Pauses or resumes the animations (P key).</summary>
    public void ToggleAnimationPause()
    {
        if (!HasAnimation) return; // on a static image P does nothing (no hidden pause state)
        IsAnimationPaused = !IsAnimationPaused;
        foreach (var (_, clock) in _animations)
        {
            if (IsAnimationPaused) clock.Controller?.Pause();
            else clock.Controller?.Resume();
        }
    }

    // ---- Zoom and pan ----

    private double ModeScale(ViewMode mode)
    {
        if (_contentSize.Width <= 0 || _contentSize.Height <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return 1;
        double fitW = ActualWidth / _contentSize.Width;
        double fitH = ActualHeight / _contentSize.Height;
        double actual = 1 / DpiScale;
        return mode switch
        {
            ViewMode.ShrinkToFit => Math.Min(Math.Min(fitW, fitH), actual),
            ViewMode.Fit => Math.Min(fitW, fitH),
            ViewMode.Fill => Math.Max(fitW, fitH),
            ViewMode.FitWidth => fitW,
            ViewMode.FitHeight => fitH,
            _ => actual,
        };
    }

    /// <summary>Reapplies the view mode (discards manual zoom and pan).</summary>
    public void ResetView()
    {
        _userAdjusted = false;
        _scale = ModeScale(ViewMode);
        double w = _contentSize.Width * _scale, h = _contentSize.Height * _scale;
        _offset = new Vector((ActualWidth - w) / 2, ViewMode == ViewMode.FitWidth ? 0 : (ActualHeight - h) / 2);
        ClampAndApply();
    }

    private double MinScale => Math.Max(Math.Min(ModeScale(ViewMode.Fit), 1 / DpiScale) / 8,
                                        32 / Math.Max(1, Math.Max(_contentSize.Width, _contentSize.Height)));

    private double MaxScale => MaxScreenZoom / DpiScale;

    public void ZoomAt(double factor, Point viewportPoint)
    {
        if (!HasContent) return;
        // The lower limit never blocks going back to 100% or to the view mode's zoom (a tiny icon has a
        // minimum above 100%: once zoomed in, it must still zoom out again).
        double low = Math.Min(Math.Min(MinScale, _scale), Math.Min(ModeScale(ViewMode), 1 / DpiScale));
        double newScale = Math.Clamp(_scale * factor, low, Math.Max(MaxScale, _scale));
        if (newScale == _scale) return;
        double f = newScale / _scale;
        var p = (Vector)viewportPoint;
        _offset = p - (p - _offset) * f;
        _scale = newScale;
        _userAdjusted = true;
        ClampAndApply();
        RebasePan();
    }

    /// <summary>
    /// Zoomed or resized while dragging to pan: the pan goes on from here, otherwise the next mouse
    /// move would put back the offset (at the old scale) the drag started from.
    /// </summary>
    private void RebasePan()
    {
        if (_drag != DragMode.Pan) return;
        // The press point stays (the pan threshold is measured from it); the offset is rebased so
        // that the next move continues from the current one.
        _dragStartOffset = _offset - (Mouse.GetPosition(this) - _dragStartViewport);
    }

    private Point ViewportCenter => new(ActualWidth / 2, ActualHeight / 2);

    public void ZoomIn() => ZoomAt(ZoomStep, ViewportCenter);
    public void ZoomOut() => ZoomAt(1 / ZoomStep, ViewportCenter);

    /// <summary>Sets an absolute zoom (1 = 100%) keeping the center.</summary>
    public void SetZoom(double screenZoom) => ZoomAt(screenZoom / DpiScale / _scale, ViewportCenter);

    public void Pan(double dx, double dy)
    {
        if (!HasContent) return;
        MoveBy(new Vector(dx, dy));
    }

    /// <summary>
    /// Scrolls. It counts as a manual adjustment (auto-fit off) only if the image really moved, and
    /// not for scrolling along the long side in "fit width" (vertical) or "fit height" (horizontal).
    /// </summary>
    private void MoveBy(Vector delta)
    {
        var before = _offset;
        _offset += delta;
        ClampAndApply();
        bool alongFit = (ViewMode == ViewMode.FitWidth && _offset.X == before.X)
                        || (ViewMode == ViewMode.FitHeight && _offset.Y == before.Y);
        if (_offset != before && !alongFit)
            _userAdjusted = true;
    }

    /// <summary>Stops a selection or pan in progress (Esc, image changed); true if there was one.</summary>
    public bool CancelDrag()
    {
        if (_drag == DragMode.None) return false;
        _drag = DragMode.None;
        _selection.Visibility = Visibility.Collapsed;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = RestingCursor;
        return true;
    }

    private void ClampAndApply()
    {
        double w = _contentSize.Width * _scale, h = _contentSize.Height * _scale;
        double vw = ActualWidth, vh = ActualHeight;
        double x = w <= vw ? (vw - w) / 2 : Math.Clamp(_offset.X, vw - w, 0);
        double y = h <= vh ? (vh - h) / 2 : Math.Clamp(_offset.Y, vh - h, 0);
        // Snap to physical pixels: at 100% the image stays sharp.
        double dpi = DpiScale;
        _offset = new Vector(Math.Round(x * dpi) / dpi, Math.Round(y * dpi) / dpi);
        _transform.Matrix = new Matrix(_scale, 0, 0, _scale, _offset.X, _offset.Y);
        UpdateScalingMode();
        UpdateOverlayThickness();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateScalingMode()
    {
        var mode = PixelatedZoom && Zoom > 2.5 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;
        foreach (var img in _images) RenderOptions.SetBitmapScalingMode(img, mode);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_userAdjusted) ClampAndApply();
        else ResetKeepingScroll();
        RebasePan();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // Also when zoomed by hand: the zoom text and the pixelated/smooth switch depend on the DPI.
        if (_userAdjusted) ClampAndApply();
        else ResetKeepingScroll();
        RebasePan();
    }

    /// <summary>
    /// Refits after a size change. In "fit width" and "fit height" the scroll position is kept
    /// (hiding the toolbar or resizing the window must not jump back to the top of a long strip).
    /// </summary>
    private void ResetKeepingScroll()
    {
        if (ViewMode is not (ViewMode.FitWidth or ViewMode.FitHeight) || _scale <= 0)
        {
            ResetView();
            return;
        }
        double topInImage = -_offset.Y / _scale, leftInImage = -_offset.X / _scale;
        ResetView();
        if (ViewMode == ViewMode.FitWidth) _offset.Y = -topInImage * _scale;
        else _offset.X = -leftInImage * _scale;
        ClampAndApply();
    }

    private Point ToImage(Point viewportPoint) =>
        new((viewportPoint.X - _offset.X) / _scale, (viewportPoint.Y - _offset.Y) / _scale);

    private Point ClampToContent(Point p) =>
        new(Math.Clamp(p.X, 0, _contentSize.Width), Math.Clamp(p.Y, 0, _contentSize.Height));

    // ---- Mouse ----

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!HasContent) return;
        e.Handled = true;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool tallerThanView = _contentSize.Height * _scale > ActualHeight + 0.5;
        // In "fit width" the wheel scrolls (long strips, webtoons); Ctrl+wheel always zooms.
        if (shift || (!ctrl && ViewMode == ViewMode.FitWidth && !_userAdjusted && tallerThanView))
        {
            double step = e.Delta / 120.0 * ActualHeight / 5;
            MoveBy(shift ? new Vector(step, 0) : new Vector(0, step));
            return;
        }
        // Proportional to the wheel movement: touchpads and smooth wheels send many small deltas.
        ZoomAt(Math.Pow(ZoomStep, e.Delta / 120.0), e.GetPosition(this));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        // Any button (a right-click opens the menu) stops the slideshow.
        UserClicked?.Invoke(this, EventArgs.Empty);
        if (e.ChangedButton == MouseButton.Middle)
        {
            DoubleClicked?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            DoubleClicked?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (!HasContent) return;

        var pos = e.GetPosition(this);
        var mods = Keyboard.Modifiers;
        _dragStartViewport = pos;
        _dragStartImage = ClampToContent(ToImage(pos));

        if ((mods & ModifierKeys.Shift) != 0)
        {
            TextRequested?.Invoke(this, ToImage(pos));
            e.Handled = true;
            return;
        }

        if ((mods & ModifierKeys.Control) != 0) StartSelection(SelectionKind.Crop);
        else if ((mods & ModifierKeys.Alt) != 0) StartSelection(SelectionKind.Rectangle);
        else if (Tool == ViewerTool.RedEye) StartSelection(SelectionKind.RedEye);
        else
        {
            _drag = DragMode.Pan;
            _panStarted = false;
            _dragStartOffset = _offset;
            Cursor = CursorOverride ?? Cursors.SizeAll;
        }
        CaptureMouse();
        e.Handled = true;
    }

    private void StartSelection(SelectionKind kind)
    {
        _drag = DragMode.Select;
        _selectionKind = kind;
        Cursor = CursorOverride ?? Cursors.Cross;
        bool rect = kind == SelectionKind.Rectangle;
        _selection.Stroke = new SolidColorBrush(rect ? RectangleColor : kind == SelectionKind.RedEye ? Colors.Red : CropColor);
        _selection.StrokeDashArray = rect ? null : new DoubleCollection { 4, 3 };
        _selection.Fill = kind == SelectionKind.Crop ? new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) : null;
        UpdateSelection(_dragStartImage);
        _selection.Visibility = Visibility.Visible;
    }

    private Rect SelectionRect(Point current) => new(_dragStartImage, ClampToContent(current));

    private void UpdateSelection(Point currentImage)
    {
        var r = SelectionRect(currentImage);
        if (_selectionKind == SelectionKind.Rectangle)
        {
            // The final rectangle pen is centered on the edge: the preview must match.
            double t = RectangleThickness;
            Canvas.SetLeft(_selection, r.X - t / 2);
            Canvas.SetTop(_selection, r.Y - t / 2);
            _selection.Width = r.Width + t;
            _selection.Height = r.Height + t;
        }
        else
        {
            Canvas.SetLeft(_selection, r.X);
            Canvas.SetTop(_selection, r.Y);
            _selection.Width = r.Width;
            _selection.Height = r.Height;
        }
        UpdateOverlayThickness();
    }

    private void UpdateOverlayThickness()
    {
        _selection.StrokeThickness = _selectionKind == SelectionKind.Rectangle ? RectangleThickness : 1.5 / _scale;
        if (_textFrame is not null)
        {
            // The border keeps a constant size on screen, so its size in image pixels changes with
            // the zoom: move the frame by the difference, so the text itself stays where it was put.
            double handle = 8 / _scale;
            double old = _textFrame.BorderThickness.Left;
            if (old > 0 && !double.IsNaN(Canvas.GetLeft(_textFrame)))
            {
                Canvas.SetLeft(_textFrame, Canvas.GetLeft(_textFrame) + old - handle);
                Canvas.SetTop(_textFrame, Canvas.GetTop(_textFrame) + old - handle);
            }
            _textFrame.BorderThickness = new Thickness(handle);
            _textFrame.CornerRadius = new CornerRadius(3 / _scale);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);
        switch (_drag)
        {
            case DragMode.Pan:
                // A click (or the tiny movement of a double-click) is not a pan: it must not
                // switch off auto-fit, or fullscreen and window resizes would stop refitting.
                if (!_panStarted && (pos - _dragStartViewport).Length < PanThreshold) break;
                _panStarted = true;
                MoveBy(_dragStartOffset + (pos - _dragStartViewport) - _offset);
                break;
            case DragMode.Select:
                UpdateSelection(ToImage(pos));
                break;
            case DragMode.MoveText when _textFrame is not null:
                var img = ToImage(pos);
                Canvas.SetLeft(_textFrame, Canvas.GetLeft(_textFrame) + img.X - _textDragStart.X);
                Canvas.SetTop(_textFrame, Canvas.GetTop(_textFrame) + img.Y - _textDragStart.Y);
                _textDragStart = img;
                break;
            case DragMode.None:
                Cursor = RestingCursor;
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var mode = _drag;
        _drag = DragMode.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = RestingCursor;

        if (mode == DragMode.Select)
        {
            var r = SelectionRect(ToImage(e.GetPosition(this)));
            _selection.Visibility = Visibility.Collapsed;
            // Edges rounded, then the size derived from them (rounding X and Width separately could
            // make the selection one pixel too large).
            int left = (int)Math.Round(r.Left), top = (int)Math.Round(r.Top);
            var rect = new Int32Rect(left, top, (int)Math.Round(r.Right) - left, (int)Math.Round(r.Bottom) - top);
            if (rect.Width >= 2 && rect.Height >= 2)
                SelectionCompleted?.Invoke(this, new SelectionEventArgs(_selectionKind, rect));
        }
        else if (mode == DragMode.MoveText)
        {
            _textBox?.Focus();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag == DragMode.Select) _selection.Visibility = Visibility.Collapsed;
        _drag = DragMode.None;
    }

    // ---- Text on the image ----

    /// <summary>Opens a text box on the image; Ctrl+Enter applies it, Esc cancels.</summary>
    public void BeginTextEdit(Point imagePoint, TextStyle style)
    {
        CancelTextEdit();
        _textStyle = style;
        _textBox = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.NoWrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = style.FontSize,
            Template = CreateBareTextBoxTemplate(),
        };
        ApplyTextStyle(style);
        _textBox.PreviewKeyDown += TextBox_PreviewKeyDown;
        _textBox.ContextMenu = CreateTextContextMenu();

        _textFrame = new Border
        {
            Child = _textBox,
            Background = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
            Cursor = Cursors.SizeAll,
            ToolTip = new ToolTip { Content = "Drag the border to move · Ctrl+Enter to apply · Esc to cancel", UseLayoutRounding = true },
            // The border too: otherwise a right-click there opens the image menu, whose commands
            // would throw the text away.
            ContextMenu = _textBox.ContextMenu,
        };
        _textFrame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource != _textFrame) return;
            _drag = DragMode.MoveText;
            _textDragStart = ToImage(e.GetPosition(this));
            CaptureMouse();
            e.Handled = true;
        };
        UpdateOverlayThickness();
        double handle = 8 / _scale;
        Canvas.SetLeft(_textFrame, imagePoint.X - handle);
        Canvas.SetTop(_textFrame, imagePoint.Y - handle);
        _content.Children.Add(_textFrame);
        _textBox.Loaded += (_, _) => Keyboard.Focus(_textBox);
    }

    public void UpdateTextStyle(TextStyle style)
    {
        _textStyle = style;
        if (_textBox is not null) ApplyTextStyle(style);
        _textBox?.Focus();
    }

    private void ApplyTextStyle(TextStyle style)
    {
        if (_textBox is null) return;
        var brush = new SolidColorBrush(style.Color);
        _textBox.FontFamily = new FontFamily(style.FontFamily);
        _textBox.FontSize = Math.Max(1, style.FontSize);
        _textBox.FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal;
        _textBox.FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal;
        _textBox.Foreground = brush;
        _textBox.CaretBrush = brush;
        _textBox.MinWidth = style.FontSize;
    }

    private static ControlTemplate CreateBareTextBoxTemplate()
    {
        var template = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(PaddingProperty, new Thickness(0));
        host.SetValue(BackgroundProperty, Brushes.Transparent);
        template.VisualTree = host;
        return template;
    }

    private ContextMenu CreateTextContextMenu()
    {
        // The menu would inherit from the text box the font of the text being written (e.g. 72 pt
        // bold), its I-beam cursor and the unrounded layout of the zoomed canvas: use the window's.
        // (The colors come from the theme's menu style.)
        var menu = new ContextMenu
        {
            FontFamily = TextElement.GetFontFamily(this),
            FontSize = TextElement.GetFontSize(this),
            FontWeight = FontWeights.Normal,
            FontStyle = FontStyles.Normal,
            Cursor = Cursors.Arrow,
            UseLayoutRounding = true,
        };
        var style = new MenuItem { Header = "Font and color…" };
        style.Click += (_, _) => TextStyleRequested?.Invoke(this, EventArgs.Empty);
        // Explicit target: the menu is also opened from the frame around the text box.
        var paste = new MenuItem { Header = "Paste", Command = ApplicationCommands.Paste, CommandTarget = _textBox, InputGestureText = "Ctrl+V" };
        var confirm = new MenuItem { Header = "Write on the image", InputGestureText = "Ctrl+Enter" };
        confirm.Click += (_, _) => CommitTextEdit();
        var cancel = new MenuItem { Header = "Cancel", InputGestureText = "Esc" };
        cancel.Click += (_, _) => CancelTextEdit();
        menu.Items.Add(style);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(confirm);
        menu.Items.Add(cancel);
        return menu;
    }

    private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            CommitTextEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelTextEdit();
            e.Handled = true;
        }
    }

    /// <summary>Asked before applying the text; false keeps the text box open (e.g. an edit is still running).</summary>
    public Func<bool>? CanCommitText { get; set; }

    public void CommitTextEdit()
    {
        if (_textBox is null || _textStyle is null) return;
        if (CanCommitText?.Invoke() == false) return;
        string text = _textBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            CancelTextEdit();
            return;
        }
        // Exact position of the first character, in image coordinates.
        _textBox.UpdateLayout();
        var charRect = _textBox.GetRectFromCharacterIndex(0);
        var origin = _textBox.TranslatePoint(
            charRect.IsEmpty ? new Point(0, 0) : new Point(charRect.Left, charRect.Top), _content);
        var s = _textStyle;
        var placement = new TextPlacement(text.Replace("\r\n", "\n"), origin.X, origin.Y,
            s.FontFamily, s.FontSize, s.Bold, s.Italic, s.Color);
        CancelTextEdit();
        TextCommitted?.Invoke(this, placement);
    }

    public void CancelTextEdit()
    {
        if (_textFrame is null) return;
        _content.Children.Remove(_textFrame);
        _textFrame = null;
        _textBox = null;
        if (_drag == DragMode.MoveText) _drag = DragMode.None;
    }
}
