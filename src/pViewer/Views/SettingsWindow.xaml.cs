using System.Globalization;
using System.Windows;
using System.Windows.Media;
using pViewer.Controls;
using pViewer.Core;
using pViewer.Services;
using pViewer.ViewModels;

namespace pViewer.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private Color _cropColor, _rectColor;
    private TextStyle _textStyle = null!;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        PathText.Text = $"File delle impostazioni: {SettingsStore.FilePath}";
        Load(settings);
    }

    private void Load(AppSettings s)
    {
        ThemeBox.SelectedIndex = (int)s.Theme;
        ViewModeBox.SelectedIndex = s.ViewMode == ViewMode.ActualSize ? 0 : (int)s.ViewMode;
        PixelatedBox.IsChecked = s.PixelatedZoom;
        AutoRotateBox.IsChecked = s.AutoRotateExif;
        ArchiveLayoutBox.SelectedIndex = (int)s.ArchiveLayout;
        ConfirmDeleteBox.IsChecked = s.ConfirmDelete;
        RecycleBox.IsChecked = s.DeleteToRecycleBin;
        ConfirmOverwriteBox.IsChecked = s.ConfirmOverwrite;
        ConfirmDiscardBox.IsChecked = s.ConfirmDiscardEdits;
        QualitySlider.Value = Math.Clamp(s.JpegQuality, 10, 100);
        _cropColor = MainViewModel.ParseColor(s.CropColor, Colors.Red);
        _rectColor = MainViewModel.ParseColor(s.RectangleColor, Colors.Red);
        RectThicknessBox.Text = s.RectangleThickness.ToString();
        BorderThicknessBox.Text = s.BorderThickness.ToString();
        SlideshowBox.Text = s.SlideshowSeconds.ToString("0.#", CultureInfo.CurrentCulture);
        _textStyle = new TextStyle(s.TextFontFamily, s.TextFontSize, s.TextBold, s.TextItalic,
            MainViewModel.ParseColor(s.TextColor, Colors.Red));
        UpdateSwatches();
    }

    private void UpdateSwatches()
    {
        CropColorButton.Background = new SolidColorBrush(_cropColor);
        RectColorButton.Background = new SolidColorBrush(_rectColor);
        TextStyleSummary.Text = $"{_textStyle.FontFamily}, {_textStyle.FontSize:0.#} px";
        TextStyleSummary.Foreground = new SolidColorBrush(_textStyle.Color);
    }

    private void CropColor_Click(object sender, RoutedEventArgs e)
    {
        if (ColorPickerDialog.Pick(this, _cropColor) is { } c) { _cropColor = c; UpdateSwatches(); }
    }

    private void RectColor_Click(object sender, RoutedEventArgs e)
    {
        if (ColorPickerDialog.Pick(this, _rectColor) is { } c) { _rectColor = c; UpdateSwatches(); }
    }

    private void TextStyle_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new TextStyleDialog(_textStyle) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is not null)
        {
            _textStyle = dlg.Result;
            UpdateSwatches();
        }
    }

    private void Defaults_Click(object sender, RoutedEventArgs e) => Load(new AppSettings());

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RectThicknessBox.Text, out int rect) || rect < 1 || rect > 500)
        {
            ErrorText.Text = "Spessore del rettangolo: da 1 a 500 pixel.";
            return;
        }
        if (!int.TryParse(BorderThicknessBox.Text, out int border) || border < 1 || border > 2000)
        {
            ErrorText.Text = "Spessore del bordo: da 1 a 2000 pixel.";
            return;
        }
        if (!double.TryParse(SlideshowBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double seconds)
            || seconds < 0.5 || seconds > 3600)
        {
            ErrorText.Text = "Presentazione: da 0,5 a 3600 secondi.";
            return;
        }

        var s = _settings;
        s.Theme = (AppTheme)Math.Max(0, ThemeBox.SelectedIndex);
        s.ViewMode = (ViewMode)Math.Max(0, ViewModeBox.SelectedIndex);
        s.PixelatedZoom = PixelatedBox.IsChecked == true;
        s.AutoRotateExif = AutoRotateBox.IsChecked == true;
        s.ArchiveLayout = (PageLayout)Math.Max(0, ArchiveLayoutBox.SelectedIndex);
        s.ConfirmDelete = ConfirmDeleteBox.IsChecked == true;
        s.DeleteToRecycleBin = RecycleBox.IsChecked == true;
        s.ConfirmOverwrite = ConfirmOverwriteBox.IsChecked == true;
        s.ConfirmDiscardEdits = ConfirmDiscardBox.IsChecked == true;
        s.JpegQuality = (int)QualitySlider.Value;
        s.CropColor = _cropColor.ToString();
        s.RectangleColor = _rectColor.ToString();
        s.RectangleThickness = rect;
        s.BorderThickness = border;
        s.SlideshowSeconds = seconds;
        s.TextFontFamily = _textStyle.FontFamily;
        s.TextFontSize = _textStyle.FontSize;
        s.TextBold = _textStyle.Bold;
        s.TextItalic = _textStyle.Italic;
        s.TextColor = _textStyle.Color.ToString();
        DialogResult = true;
    }
}
