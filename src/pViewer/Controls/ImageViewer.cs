using System.Windows;
using System.Windows.Controls;
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
/// Visualizzatore con zoom e pan. Il contenuto è espresso in pixel dell'immagine e viene
/// scalato/spostato con una trasformazione (accelerata dalla GPU): l'immagine non viene mai
/// ricampionata per mostrarla. Gestisce anche i gesti di modifica:
/// Ctrl+trascina = ritaglio, Alt+trascina = rettangolo, Maiusc+clic = testo, strumento occhi rossi.
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

    // Stato del trascinamento corrente.
    private enum DragMode { None, Pan, Select, MoveText }
    private DragMode _drag;
    private SelectionKind _selectionKind;
    private Point _dragStartViewport;
    private Point _dragStartImage;
    private Vector _dragStartOffset;
    private Point _textDragStart;

    // Testo in modifica.
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
        Child = _host;
    }

    // ---- Proprietà ----

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

    /// <summary>Oltre il 250% mostra i pixel netti invece che sfumati.</summary>
    public bool PixelatedZoom
    {
        get => (bool)GetValue(PixelatedZoomProperty);
        set => SetValue(PixelatedZoomProperty, value);
    }

    public ViewerTool Tool { get; set; }
    public Color CropColor { get; set; } = Colors.Red;
    public Color RectangleColor { get; set; } = Colors.Red;
    public double RectangleThickness { get; set; } = 5;

    public bool HasContent => _pages.Count > 0;
    public bool IsEditingText => _textFrame is not null;

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Pixel dello schermo per pixel dell'immagine (1 = 100%).</summary>
    public double Zoom => _scale * DpiScale;

    public event EventHandler? ZoomChanged;
    public event EventHandler<SelectionEventArgs>? SelectionCompleted;
    public event EventHandler<Point>? TextRequested;
    public event EventHandler<TextPlacement>? TextCommitted;
    public event EventHandler? TextStyleRequested;
    public event EventHandler? DoubleClicked;
    /// <summary>Qualsiasi clic: serve a fermare la presentazione.</summary>
    public event EventHandler? UserClicked;

    // ---- Contenuto ----

    /// <param name="preserveView">Mantiene zoom e posizione se le dimensioni non cambiano (es. dopo una modifica).</param>
    /// <param name="logicalSize">Dimensione da usare al posto di quella reale (anteprima a bassa risoluzione).</param>
    /// <param name="animations">Per ogni pagina, i fotogrammi da animare (null = immagine statica).</param>
    public void SetPages(IReadOnlyList<BitmapSource> pages, bool preserveView = false, Size? logicalSize = null,
                         IReadOnlyList<ImageAnimation?>? animations = null)
    {
        var oldSize = _contentSize;
        _pages = pages;
        StopAnimations();
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
            Canvas.SetTop(img, (height - PageHeight(page)) / 2);
            _content.Children.Insert(insertAt++, img);
            _images.Add(img);
            if (animations is not null && insertAt - 1 < animations.Count && animations[insertAt - 1] is { } anim)
                StartAnimation(img, anim);
            x += PageWidth(page);
        }
        _contentSize = new Size(x, height);
        _content.Width = x;
        _content.Height = height;

        if (preserveView && _userAdjusted && oldSize == _contentSize)
        {
            ClampAndApply();
        }
        else
        {
            CancelTextEdit();
            ResetView();
        }
        UpdateScalingMode();
    }

    public void Clear() => SetPages([]);

    // ---- Animazioni (GIF, WebP, APNG) ----

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

    /// <summary>Mette in pausa o riprende le animazioni (tasto P).</summary>
    public void ToggleAnimationPause()
    {
        IsAnimationPaused = !IsAnimationPaused;
        foreach (var (_, clock) in _animations)
        {
            if (IsAnimationPaused) clock.Controller?.Pause();
            else clock.Controller?.Resume();
        }
    }

    // ---- Zoom e pan ----

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

    /// <summary>Riapplica la modalità di vista (annulla zoom e pan manuali).</summary>
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
        double newScale = Math.Clamp(_scale * factor, Math.Min(MinScale, _scale), Math.Max(MaxScale, _scale));
        if (newScale == _scale) return;
        double f = newScale / _scale;
        var p = (Vector)viewportPoint;
        _offset = p - (p - _offset) * f;
        _scale = newScale;
        _userAdjusted = true;
        ClampAndApply();
    }

    private Point ViewportCenter => new(ActualWidth / 2, ActualHeight / 2);

    public void ZoomIn() => ZoomAt(ZoomStep, ViewportCenter);
    public void ZoomOut() => ZoomAt(1 / ZoomStep, ViewportCenter);

    /// <summary>Imposta uno zoom assoluto (1 = 100%) mantenendo il centro.</summary>
    public void SetZoom(double screenZoom) => ZoomAt(screenZoom / DpiScale / _scale, ViewportCenter);

    public void Pan(double dx, double dy)
    {
        if (!HasContent) return;
        _offset += new Vector(dx, dy);
        _userAdjusted = true;
        ClampAndApply();
    }

    private void ClampAndApply()
    {
        double w = _contentSize.Width * _scale, h = _contentSize.Height * _scale;
        double vw = ActualWidth, vh = ActualHeight;
        double x = w <= vw ? (vw - w) / 2 : Math.Clamp(_offset.X, vw - w, 0);
        double y = h <= vh ? (vh - h) / 2 : Math.Clamp(_offset.Y, vh - h, 0);
        // Allinea ai pixel fisici: al 100% l'immagine resta nitida.
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
        else ResetView();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (!_userAdjusted) ResetView();
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
        // In "adatta alla larghezza" la rotella scorre (strisce lunghe, webtoon); Ctrl+rotella fa sempre zoom.
        if (shift || (!ctrl && ViewMode == ViewMode.FitWidth && !_userAdjusted && tallerThanView))
        {
            double step = e.Delta / 120.0 * ActualHeight / 5;
            if (shift) { _offset += new Vector(step, 0); _userAdjusted = true; ClampAndApply(); }
            else { _offset += new Vector(0, step); ClampAndApply(); }
            return;
        }
        ZoomAt(e.Delta > 0 ? ZoomStep : 1 / ZoomStep, e.GetPosition(this));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle)
        {
            DoubleClicked?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        UserClicked?.Invoke(this, EventArgs.Empty);
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
            _dragStartOffset = _offset;
            Cursor = Cursors.SizeAll;
        }
        CaptureMouse();
        e.Handled = true;
    }

    private void StartSelection(SelectionKind kind)
    {
        _drag = DragMode.Select;
        _selectionKind = kind;
        Cursor = Cursors.Cross;
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
            // La penna del rettangolo finale è centrata sul bordo: l'anteprima deve coincidere.
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
            double handle = 8 / _scale;
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
                _offset = _dragStartOffset + (pos - _dragStartViewport);
                _userAdjusted = true;
                ClampAndApply();
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
                Cursor = Tool == ViewerTool.RedEye ? Cursors.Cross : null;
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var mode = _drag;
        _drag = DragMode.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = Tool == ViewerTool.RedEye ? Cursors.Cross : null;

        if (mode == DragMode.Select)
        {
            var r = SelectionRect(ToImage(e.GetPosition(this)));
            _selection.Visibility = Visibility.Collapsed;
            var rect = new Int32Rect((int)Math.Round(r.X), (int)Math.Round(r.Y),
                                     (int)Math.Round(r.Width), (int)Math.Round(r.Height));
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

    // ---- Testo sull'immagine ----

    /// <summary>Apre una casella di testo sull'immagine; si conferma con Ctrl+Invio, si annulla con Esc.</summary>
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
            ToolTip = "Trascina il bordo per spostare · Ctrl+Invio conferma · Esc annulla",
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
        var menu = new ContextMenu();
        var style = new MenuItem { Header = "Carattere e colore…" };
        style.Click += (_, _) => TextStyleRequested?.Invoke(this, EventArgs.Empty);
        var paste = new MenuItem { Header = "Incolla", Command = ApplicationCommands.Paste, InputGestureText = "Ctrl+V" };
        var confirm = new MenuItem { Header = "Scrivi sull'immagine", InputGestureText = "Ctrl+Invio" };
        confirm.Click += (_, _) => CommitTextEdit();
        var cancel = new MenuItem { Header = "Annulla", InputGestureText = "Esc" };
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

    public void CommitTextEdit()
    {
        if (_textBox is null || _textStyle is null) return;
        string text = _textBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            CancelTextEdit();
            return;
        }
        // Posizione esatta del primo carattere, in coordinate dell'immagine.
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
