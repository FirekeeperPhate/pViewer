using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using pViewer.Services;

namespace pViewer.Views;

/// <summary>The "Keyboard shortcuts" section of the settings.</summary>
public partial class SettingsWindow
{
    /// <summary>Working copy of the user's shortcuts (only the commands changed from the defaults).</summary>
    private Dictionary<string, List<string>> _hotkeys = [];
    private readonly Dictionary<string, TextBox> _hotkeyBoxes = [];
    private string? _recording;

    private void LoadHotkeys(AppSettings s)
    {
        _hotkeys = s.Hotkeys?.ToDictionary(p => p.Key, p => p.Value.ToList()) ?? [];
        HotkeyNote.Text = null;
        if (_hotkeyBoxes.Count == 0) BuildHotkeyRows();
        RefreshHotkeys();
    }

    private void BuildHotkeyRows()
    {
        foreach (var group in Hotkeys.All.GroupBy(c => c.Group))
        {
            HotkeysPanel.Children.Add(new TextBlock
            {
                Text = group.Key, FontWeight = FontWeights.SemiBold, Opacity = 0.8, Margin = new Thickness(0, 10, 0, 4),
            });
            foreach (var command in group)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var name = new TextBlock { Text = command.Name, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
                var keys = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = false, Tag = command.Id, Cursor = Cursors.Arrow };
                keys.PreviewKeyDown += HotkeyBox_PreviewKeyDown;
                keys.LostKeyboardFocus += (_, _) => { if (_recording == command.Id) StopRecording(); };
                _hotkeyBoxes[command.Id] = keys;

                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
                buttons.Children.Add(SmallButton("+", "Add a shortcut: click, then press the keys", () => StartRecording(command.Id)));
                buttons.Children.Add(SmallButton("✕", "Remove the shortcuts of this command", () => SetKeys(command.Id, [])));
                buttons.Children.Add(SmallButton("↺", "Restore the default", () => SetKeys(command.Id, command.DefaultGestures)));

                Grid.SetColumn(keys, 1);
                Grid.SetColumn(buttons, 2);
                row.Children.Add(name);
                row.Children.Add(keys);
                row.Children.Add(buttons);
                HotkeysPanel.Children.Add(row);
            }
        }
    }

    private static Button SmallButton(string text, string tip, Action click)
    {
        var button = new Button { Content = text, ToolTip = tip, MinWidth = 32, Padding = new Thickness(6, 3, 6, 4), Margin = new Thickness(4, 0, 0, 0) };
        button.Click += (_, _) => click();
        return button;
    }

    private void RefreshHotkeys()
    {
        foreach (var (id, box) in _hotkeyBoxes)
        {
            if (id == _recording) continue;
            var keys = Hotkeys.GesturesOf(id, _hotkeys);
            box.Text = keys.Count == 0 ? "—" : string.Join(",  ", keys.Select(k => k.Display));
            box.ToolTip = box.Text;
        }
    }

    private void SetKeys(string id, IEnumerable<Shortcut> keys)
    {
        StopRecording();
        HotkeyNote.Text = null;
        Hotkeys.Set(_hotkeys, id, keys);
        RefreshHotkeys();
    }

    private void StartRecording(string id)
    {
        StopRecording();
        HotkeyNote.Text = null;
        _recording = id;
        var box = _hotkeyBoxes[id];
        box.Text = "Press the keys…";
        box.Focus();
    }

    private void StopRecording()
    {
        if (_recording is null) return;
        _recording = null;
        RefreshHotkeys();
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: string id } || id != _recording) return;
        // Every key goes to the recording, Tab, Enter and Esc included (not to the dialog).
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Shortcut.IsModifierKey(key)) return; // wait for the key that goes with Ctrl/Shift/Alt
        var gesture = new Shortcut(key, Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt));
        _recording = null;
        var previous = Hotkeys.Assign(_hotkeys, id, gesture);
        RefreshHotkeys();
        HotkeyNote.Text = previous is null ? null
            : $"{gesture.Display} was removed from «{previous.Name}».";
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
        _hotkeys.Clear();
        HotkeyNote.Text = null;
        RefreshHotkeys();
    }
}
