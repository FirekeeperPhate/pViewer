using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace pViewer.Views;

public partial class ResizeDialog : Window
{
    private const int MaxSide = 30000;
    private readonly int _origW, _origH;
    private bool _updating;

    public ResizeDialog(int width, int height)
    {
        _origW = width;
        _origH = height;
        InitializeComponent();
        OriginalText.Text = $"Current size: {width} × {height} px";
        Set(WidthBox, width);
        Set(HeightBox, height);
        Set(PercentBox, 100);
        Loaded += (_, _) => { WidthBox.Focus(); WidthBox.SelectAll(); };
    }

    public int ResultWidth { get; private set; }
    public int ResultHeight { get; private set; }

    private void Set(TextBox box, double value)
    {
        _updating = true;
        // A derived side never rounds to 0 (a 1 px wide image at 50%, a thin banner made smaller):
        // OK would refuse it. The percentage keeps its decimals.
        box.Text = box == PercentBox
            ? Math.Round(value, 2).ToString("0.##", CultureInfo.CurrentCulture)
            : Math.Max(1, Math.Round(value, MidpointRounding.AwayFromZero)).ToString(CultureInfo.CurrentCulture);
        _updating = false;
    }

    private static bool TryRead(TextBox box, out double value) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && value > 0 && double.IsFinite(value);

    // Ticking the box again re-derives the height from the width, so OK never applies a
    // distorted size while "Keep aspect ratio" is shown as ticked.
    private void KeepRatio_Checked(object sender, RoutedEventArgs e)
    {
        if (!_updating && WidthBox is not null && TryRead(WidthBox, out double w)) Set(HeightBox, w * _origH / _origW);
    }

    private void WidthBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || !TryRead(WidthBox, out double w)) return;
        if (KeepRatio.IsChecked == true) Set(HeightBox, w * _origH / _origW);
        Set(PercentBox, w * 100 / _origW);
    }

    private void HeightBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || !TryRead(HeightBox, out double h)) return;
        if (KeepRatio.IsChecked == true) Set(WidthBox, h * _origW / _origH);
        Set(PercentBox, h * 100 / _origH);
    }

    private void PercentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || !TryRead(PercentBox, out double p)) return;
        Set(WidthBox, _origW * p / 100);
        Set(HeightBox, _origH * p / 100);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRead(WidthBox, out double w) || !TryRead(HeightBox, out double h)
            || Math.Round(w) < 1 || Math.Round(h) < 1 || w > MaxSide || h > MaxSide)
        {
            ErrorText.Text = $"Enter a size between 1 and {MaxSide} pixels.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        ResultWidth = (int)Math.Round(w);
        ResultHeight = (int)Math.Round(h);
        DialogResult = true;
    }
}
