using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace pViewer.Views;

/// <summary>List of keyboard shortcuts and mouse gestures.</summary>
public sealed class HelpWindow : Window
{
    private static readonly (string Section, (string Keys, string Action)[] Items)[] Shortcuts =
    [
        ("Navigation", [
            ("→  Space  E  PgUp", "Next image"),
            ("←  Q  PgDn", "Previous image"),
            ("Home  /  End", "First / last image"),
            ("M  /  C", "Manga / comic mode (two pages)"),
            ("F12  /  Shift+F12", "Shift the page pair forward / back by one page"),
            ("Ctrl+O", "Open a file, folder or archive"),
            ("Drop a file", "Opens images, folders and archives"),
        ]),
        ("View", [
            ("Wheel  /  +  −", "Zoom (towards the pointer with the wheel)"),
            ("Drag", "Pan the zoomed image"),
            ("A", "Actual size ↔ preferred view"),
            ("Ctrl+0  /  Num 5", "Reset the view"),
            ("Ctrl+1", "Zoom to 100%"),
            ("Num 4 8 6 2", "Pan the view"),
            ("F11  /  double-click", "Full screen"),
            ("Alt+Enter", "Maximize / restore the window"),
            ("W", "White background"),
            ("T", "Show / hide the toolbar"),
            ("P", "Pause / resume animations (GIF, WebP, APNG)"),
            ("Esc", "Stops the slideshow, exits the red-eye tool or full screen; otherwise closes pViewer"),
        ]),
        ("Edit", [
            ("↑  /  ↓", "Rotate right / left"),
            ("Alt+↑  /  Alt+↓", "Flip horizontally / vertically"),
            ("Ctrl+drag", "Crop"),
            ("Alt+drag", "Draw a rectangle"),
            ("Tab", "Fill the last rectangle (to hide sensitive data)"),
            ("Shift+click", "Write text (Ctrl+Enter to apply, Esc to cancel)"),
            ("R", "Red-eye correction (drag around the eye)"),
            ("Ctrl+R", "Resize"),
            ("Ctrl+I  /  Ctrl+G", "Invert / grayscale"),
            ("Ctrl+Z  /  Ctrl+Y", "Undo / redo"),
            ("F5", "Reload the original (discards changes)"),
        ]),
        ("File", [
            ("Ctrl+S", "Save"),
            ("Ctrl+Shift+S", "Save as"),
            ("F2", "Rename"),
            ("Del", "Delete (to the Recycle Bin)"),
            ("Ctrl+C  /  Ctrl+V", "Copy the image / paste from the clipboard"),
            ("I", "EXIF and metadata"),
            ("Ctrl+W  /  Ctrl+Q", "Close pViewer"),
        ]),
    ];

    public HelpWindow()
    {
        Title = "Keyboard shortcuts";
        Width = 620;
        Height = 700;
        MinWidth = 400;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(24, 8, 24, 24) };
        foreach (var (section, items) in Shortcuts)
        {
            panel.Children.Add(new TextBlock { Text = section, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < items.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var keys = new TextBlock { Text = items[i].Keys, FontFamily = new FontFamily("Segoe UI Semibold"), Margin = new Thickness(0, 3, 12, 3), Opacity = 0.9 };
                var action = new TextBlock { Text = items[i].Action, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3), Opacity = 0.8 };
                Grid.SetRow(keys, i);
                Grid.SetRow(action, i);
                Grid.SetColumn(action, 1);
                grid.Children.Add(keys);
                grid.Children.Add(action);
            }
            panel.Children.Add(grid);
        }
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Escape or System.Windows.Input.Key.F1) Close(); };
    }
}
