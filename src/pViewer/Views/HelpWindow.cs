using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using pViewer.Services;

namespace pViewer.Views;

/// <summary>List of keyboard shortcuts (the user's current ones) and mouse gestures.</summary>
public sealed class HelpWindow : Window
{
    /// <summary>Mouse gestures, not configurable: shown after the keys of each section.</summary>
    private static readonly Dictionary<string, (string Keys, string Action)[]> MouseGestures = new()
    {
        ["Navigation"] = [("Drop a file", "Opens images, folders and archives")],
        ["View"] =
        [
            ("Wheel", "Zoom towards the pointer (scrolls instead in “Fit width” until you zoom)"),
            ("Ctrl+wheel", "Always zoom"),
            ("Shift+wheel", "Scroll sideways"),
            ("Drag", "Pan the zoomed image"),
            ("Double-click  /  middle click", "Full screen (with no image: open a file)"),
        ],
        ["Edit"] =
        [
            ("Ctrl+drag", "Crop"),
            ("Alt+drag", "Draw a rectangle"),
            ("Shift+click", "Write text (Ctrl+Enter to apply, Esc to cancel)"),
        ],
    };

    public HelpWindow(IReadOnlyDictionary<string, List<string>>? hotkeys)
    {
        Title = "Keyboard shortcuts";
        Width = 620;
        Height = Math.Min(700, SystemParameters.WorkArea.Height * 0.9); // fits 1080p screens at 150%
        MinWidth = 400;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(24, 8, 24, 24) };
        foreach (var group in Hotkeys.All.GroupBy(c => c.Group))
        {
            var items = group
                .Select(c => (Keys: string.Join("  ", Hotkeys.GesturesOf(c.Id, hotkeys).Select(g => g.Display)), Action: c.Name))
                .Select(i => i.Keys.Length == 0 ? (Keys: "—", i.Action) : i)
                .Concat(MouseGestures.GetValueOrDefault(group.Key) ?? [])
                .ToList();
            panel.Children.Add(new TextBlock { Text = group.Key, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < items.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var keys = new TextBlock { Text = items[i].Keys, FontFamily = new FontFamily("Segoe UI Semibold"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 12, 3), Opacity = 0.9 };
                var action = new TextBlock { Text = items[i].Action, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3), Opacity = 0.8 };
                Grid.SetRow(keys, i);
                Grid.SetRow(action, i);
                Grid.SetColumn(action, 1);
                grid.Children.Add(keys);
                grid.Children.Add(action);
            }
            panel.Children.Add(grid);
        }
        panel.Children.Add(new TextBlock
        {
            Text = "Shortcuts can be changed in Settings › Keyboard shortcuts.",
            Opacity = 0.6, Margin = new Thickness(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        // Esc, or the keys that opened it (F1 unless changed).
        var helpKeys = Hotkeys.GesturesOf("Help", hotkeys);
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape
                || helpKeys.Contains(new Shortcut(e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key, System.Windows.Input.Keyboard.Modifiers)))
                Close();
        };
    }
}
