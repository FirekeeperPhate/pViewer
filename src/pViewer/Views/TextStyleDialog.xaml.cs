using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using pViewer.Controls;

namespace pViewer.Views;

public partial class TextStyleDialog : Window
{
    private Color _color;
    private bool _ready;

    public TextStyleDialog(TextStyle current)
    {
        InitializeComponent();
        var language = System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);
        foreach (var name in Fonts.SystemFontFamilies
                     .Select(f => f.FamilyNames.TryGetValue(language, out var n) ? n : f.Source)
                     .Distinct()
                     .Order(StringComparer.CurrentCultureIgnoreCase))
            FontBox.Items.Add(name);
        FontBox.Text = current.FontFamily;
        SizeBox.Text = current.FontSize.ToString("0.#", CultureInfo.CurrentCulture);
        BoldBox.IsChecked = current.Bold;
        ItalicBox.IsChecked = current.Italic;
        _color = current.Color;
        _ready = true;
        UpdatePreview();
    }

    public TextStyle? Result { get; private set; }

    private void UpdatePreview()
    {
        if (!_ready) return;
        ColorSwatch.Background = new SolidColorBrush(_color);
        PreviewText.Foreground = new SolidColorBrush(_color);
        try { PreviewText.FontFamily = new FontFamily(FontBox.Text); } catch (ArgumentException) { }
        PreviewText.FontWeight = BoldBox.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;
        PreviewText.FontStyle = ItalicBox.IsChecked == true ? FontStyles.Italic : FontStyles.Normal;
        if (double.TryParse(SizeBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double size) && size > 0)
            PreviewText.FontSize = Math.Min(size, 48);
    }

    private void Any_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FontBox.SelectedItem is string s) FontBox.Text = s;
        UpdatePreview();
    }

    private void Any_LostFocus(object sender, RoutedEventArgs e) => UpdatePreview();
    private void Any_Click(object sender, RoutedEventArgs e) => UpdatePreview();
    private void Size_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        var picked = ColorPickerDialog.Pick(this, _color);
        if (picked is null) return;
        _color = picked.Value;
        UpdatePreview();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(SizeBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double size) || size < 1 || size > 2000)
        {
            ErrorText.Text = "Dimensione: da 1 a 2000 pixel.";
            return;
        }
        string font = FontBox.Text.Trim();
        if (font.Length == 0)
        {
            ErrorText.Text = "Scegli un carattere.";
            return;
        }
        Result = new TextStyle(font, size, BoldBox.IsChecked == true, ItalicBox.IsChecked == true, _color);
        DialogResult = true;
    }
}
