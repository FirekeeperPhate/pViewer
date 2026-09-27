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
        OriginalText.Text = $"Dimensioni attuali: {width} × {height} px";
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
        box.Text = Math.Round(value).ToString(CultureInfo.CurrentCulture);
        _updating = false;
    }

    private static bool TryRead(TextBox box, out double value) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && value > 0;

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
            ErrorText.Text = $"Inserisci dimensioni tra 1 e {MaxSide} pixel.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        ResultWidth = (int)Math.Round(w);
        ResultHeight = (int)Math.Round(h);
        DialogResult = true;
    }
}
