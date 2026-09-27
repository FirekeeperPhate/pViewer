using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using pViewer.ViewModels;

namespace pViewer.Views;

/// <summary>Tavolozza di colori con campo esadecimale (WPF non ha un selettore colore di serie).</summary>
public sealed class ColorPickerDialog : Window
{
    private static readonly string[] Palette =
    [
        "#FFFFFFFF", "#FFD9D9D9", "#FFA6A6A6", "#FF737373", "#FF404040", "#FF000000",
        "#FFFF3B30", "#FFFF9500", "#FFFFCC00", "#FF34C759", "#FF00C7BE", "#FF30B0C7",
        "#FF007AFF", "#FF5856D6", "#FFAF52DE", "#FFFF2D55", "#FFA2845E", "#FF8E8E93",
        "#FFB00020", "#FFE65100", "#FF1B5E20", "#FF0D47A1", "#FF4A148C", "#FF3E2723",
    ];

    private readonly TextBox _hex = new() { Width = 120 };
    private readonly Border _preview = new() { Width = 40, Height = 28, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
    private readonly TextBlock _error = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x5E)), Margin = new Thickness(0, 6, 0, 0) };

    public ColorPickerDialog(Color initial)
    {
        Title = "Colore";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Result = initial;

        var grid = new UniformGrid { Columns = 6 };
        foreach (string hex in Palette)
        {
            var color = MainViewModel.ParseColor(hex, Colors.Black);
            var swatch = new Button
            {
                Width = 36, Height = 28, Margin = new Thickness(3), MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
                Background = new SolidColorBrush(color), ToolTip = color.ToString(),
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            };
            swatch.Click += (_, _) => SetColor(color);
            swatch.MouseDoubleClick += (_, _) => { SetColor(color); DialogResult = true; };
            grid.Children.Add(swatch);
        }

        _hex.TextChanged += (_, _) =>
        {
            if (TryParse(_hex.Text, out var c)) { _preview.Background = new SolidColorBrush(c); _error.Text = ""; }
        };

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90 };
        ok.SetResourceReference(StyleProperty, "AccentButtonStyle");
        ok.Click += (_, _) =>
        {
            if (!TryParse(_hex.Text, out var c)) { _error.Text = "Colore non valido (es. #FF3B30)."; return; }
            Result = c;
            DialogResult = true;
        };
        var cancel = new Button { Content = "Annulla", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };

        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(3, 12, 0, 0) };
        hexRow.Children.Add(new TextBlock { Text = "Esadecimale", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        hexRow.Children.Add(_hex);
        hexRow.Children.Add(new Border { Width = 10 });
        hexRow.Children.Add(_preview);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(grid);
        root.Children.Add(hexRow);
        root.Children.Add(_error);
        root.Children.Add(buttons);
        Content = root;

        SetColor(initial);
    }

    public Color Result { get; private set; }

    private void SetColor(Color c)
    {
        _hex.Text = c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : c.ToString();
        _preview.Background = new SolidColorBrush(c);
    }

    private static bool TryParse(string text, out Color color)
    {
        color = default;
        text = text.Trim();
        if (text.Length > 0 && text[0] != '#') text = "#" + text;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static Color? Pick(Window owner, Color initial)
    {
        var dlg = new ColorPickerDialog(initial) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }
}
