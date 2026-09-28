using System.Windows.Input;

namespace pViewer.Services;

/// <summary>A key with its modifiers, e.g. Ctrl+Shift+S.</summary>
public readonly record struct Shortcut(Key Key, ModifierKeys Modifiers)
{
    /// <summary>Settings form: "Ctrl+Shift+S", "Alt+Right", "PageUp" (WPF key names).</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        // Keys with two names (PageDown = Next, Enter = Return): always the familiar one.
        parts.Add(Key switch { Key.Return => "Enter", Key.PageUp => "PageUp", Key.PageDown => "PageDown", _ => Key.ToString() });
        return string.Join("+", parts);
    }

    /// <summary>Form shown to the user: "Ctrl+Shift+S", "Alt+→", "PgUp", "Num +".</summary>
    public string Display
    {
        get
        {
            string key = Key switch
            {
                Key.Right => "→", Key.Left => "←", Key.Up => "↑", Key.Down => "↓",
                Key.PageUp => "PgUp", Key.PageDown => "PgDn", Key.Delete => "Del", Key.Insert => "Ins",
                Key.Escape => "Esc", Key.Return => "Enter", Key.Back => "Backspace",
                Key.OemPlus => "+", Key.OemMinus => "−", Key.OemComma => ",", Key.OemPeriod => ".",
                Key.Add => "Num +", Key.Subtract => "Num −", Key.Multiply => "Num *", Key.Divide => "Num /",
                Key.Decimal => "Num .",
                >= Key.D0 and <= Key.D9 => ((char)('0' + (Key - Key.D0))).ToString(),
                >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (Key - Key.NumPad0),
                _ => Key.ToString(),
            };
            string mods = (Modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "")
                          + (Modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "")
                          + (Modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "");
            return mods + key;
        }
    }

    public static bool TryParse(string? text, out Shortcut gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = ModifierKeys.None;
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        // "Num +" style keys never reach here: the settings form uses key names ("Add").
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                default: return false;
            }
        }
        if (!Enum.TryParse(parts[^1], ignoreCase: true, out Key key) || key == Key.None || IsModifierKey(key)) return false;
        gesture = new Shortcut(key == Key.Enter ? Key.Return : key, mods);
        return true;
    }

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System or Key.ImeProcessed or Key.DeadCharProcessed;
}

/// <summary>A command that can be bound to keys, with its default keys.</summary>
public sealed record HotkeyCommand(string Id, string Group, string Name, string[] Defaults)
{
    public IReadOnlyList<Shortcut> DefaultGestures { get; } =
        Defaults.Select(d => Shortcut.TryParse(d, out var g) ? g : throw new ArgumentException(d)).ToArray();
}

/// <summary>
/// The keyboard shortcuts: the list of commands with their default keys, and the user's changes
/// (<see cref="AppSettings.Hotkeys"/> keeps only the commands whose keys differ from the default).
/// </summary>
public static class Hotkeys
{
    public static readonly HotkeyCommand[] All =
    [
        new("Next", "Navigation", "Next image", ["Right", "Space", "E", "PageUp", "Alt+Right"]),
        new("Previous", "Navigation", "Previous image", ["Left", "Q", "PageDown", "Alt+Left"]),
        new("First", "Navigation", "First image", ["Home"]),
        new("Last", "Navigation", "Last image", ["End"]),
        new("Manga", "Navigation", "Manga mode (two pages, right to left)", ["M"]),
        new("Comic", "Navigation", "Comic mode (two pages, left to right)", ["C"]),
        new("ShiftForward", "Navigation", "Shift the page pair forward", ["F12"]),
        new("ShiftBack", "Navigation", "Shift the page pair back", ["Shift+F12"]),
        new("Open", "Navigation", "Open a file, folder or archive", ["Ctrl+O"]),

        new("ZoomIn", "View", "Zoom in", ["OemPlus", "Add", "Ctrl+OemPlus", "Ctrl+Add"]),
        new("ZoomOut", "View", "Zoom out", ["OemMinus", "Subtract", "Ctrl+OemMinus", "Ctrl+Subtract"]),
        new("ResetView", "View", "Reset the view", ["Ctrl+D0", "Ctrl+NumPad0", "NumPad5"]),
        new("Zoom100", "View", "Zoom to 100%", ["Ctrl+D1", "Ctrl+NumPad1"]),
        new("ActualSize", "View", "Actual size ↔ preferred view", ["A"]),
        new("PanLeft", "View", "Pan left", ["NumPad4"]),
        new("PanRight", "View", "Pan right", ["NumPad6"]),
        new("PanUp", "View", "Pan up", ["NumPad8"]),
        new("PanDown", "View", "Pan down", ["NumPad2"]),
        new("FullScreen", "View", "Full screen", ["F11"]),
        new("Maximize", "View", "Maximize / restore the window", ["Alt+Enter"]),
        new("WhiteBackground", "View", "White background", ["W"]),
        new("Toolbar", "View", "Show / hide the toolbar", ["T"]),
        new("PauseAnimation", "View", "Pause / resume animations", ["P"]),
        new("Escape", "View", "Stop slideshow, exit a tool or full screen, otherwise close", ["Escape"]),

        new("RotateRight", "Edit", "Rotate right", ["Up"]),
        new("RotateLeft", "Edit", "Rotate left", ["Down"]),
        new("FlipHorizontal", "Edit", "Flip horizontally", ["Alt+Up"]),
        new("FlipVertical", "Edit", "Flip vertically", ["Alt+Down"]),
        new("FillRectangle", "Edit", "Fill the last rectangle", ["Tab"]),
        new("RedEye", "Edit", "Red-eye correction", ["R"]),
        new("Resize", "Edit", "Resize", ["Ctrl+R"]),
        new("Invert", "Edit", "Invert colors", ["Ctrl+I"]),
        new("Grayscale", "Edit", "Grayscale", ["Ctrl+G"]),
        new("Undo", "Edit", "Undo", ["Ctrl+Z"]),
        new("Redo", "Edit", "Redo", ["Ctrl+Y", "Ctrl+Shift+Z"]),
        new("Reload", "Edit", "Reload the original (discards changes)", ["F5"]),

        new("Save", "File", "Save", ["Ctrl+S"]),
        new("SaveAs", "File", "Save as", ["Ctrl+Shift+S", "Ctrl+Alt+S"]),
        new("Rename", "File", "Rename", ["F2"]),
        new("Delete", "File", "Delete", ["Delete"]),
        new("Copy", "File", "Copy the image", ["Ctrl+C"]),
        new("Paste", "File", "Paste from the clipboard", ["Ctrl+V"]),
        new("Metadata", "File", "EXIF and metadata", ["I"]),
        new("Help", "File", "Keyboard shortcuts", ["F1"]),
        new("Close", "File", "Close pViewer", ["Ctrl+W", "Ctrl+Q"]),
    ];

    private static readonly Dictionary<string, HotkeyCommand> ById = All.ToDictionary(c => c.Id);

    public static HotkeyCommand? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>The keys of a command: the user's ones if changed, otherwise the defaults.</summary>
    public static IReadOnlyList<Shortcut> GesturesOf(string id, IReadOnlyDictionary<string, List<string>>? overrides)
    {
        if (overrides is not null && overrides.TryGetValue(id, out var custom))
        {
            var list = new List<Shortcut>();
            foreach (string text in custom)
                if (Shortcut.TryParse(text, out var g) && !list.Contains(g)) list.Add(g);
            return list;
        }
        return Find(id)?.DefaultGestures ?? [];
    }

    /// <summary>Key → command. With a hand-edited duplicate, the first command in the list wins.</summary>
    public static Dictionary<Shortcut, string> BuildMap(IReadOnlyDictionary<string, List<string>>? overrides)
    {
        var map = new Dictionary<Shortcut, string>();
        foreach (var command in All)
            foreach (var gesture in GesturesOf(command.Id, overrides))
                map.TryAdd(gesture, command.Id);
        return map;
    }

    /// <summary>Text for menus and tooltips: the first keys of the command ("" if it has none).</summary>
    public static string DisplayText(string id, IReadOnlyDictionary<string, List<string>>? overrides, int max = 1) =>
        string.Join(", ", GesturesOf(id, overrides).Take(max).Select(g => g.Display));

    /// <summary>
    /// Stores the keys of a command, keeping only the differences from the defaults.
    /// </summary>
    public static void Set(Dictionary<string, List<string>> overrides, string id, IEnumerable<Shortcut> gestures)
    {
        var list = gestures.Distinct().ToList();
        var command = Find(id);
        if (command is not null && list.SequenceEqual(command.DefaultGestures)) overrides.Remove(id);
        else overrides[id] = list.Select(g => g.ToString()).ToList();
    }

    /// <summary>
    /// Adds a key to a command, removing it from any other command that had it.
    /// </summary>
    /// <returns>The command the key was taken from, if any.</returns>
    public static HotkeyCommand? Assign(Dictionary<string, List<string>> overrides, string id, Shortcut gesture)
    {
        HotkeyCommand? previous = null;
        foreach (var other in All)
        {
            if (other.Id == id) continue;
            var keys = GesturesOf(other.Id, overrides);
            if (!keys.Contains(gesture)) continue;
            Set(overrides, other.Id, keys.Where(k => k != gesture));
            previous = other;
        }
        var own = GesturesOf(id, overrides);
        if (!own.Contains(gesture)) Set(overrides, id, own.Append(gesture));
        return previous;
    }
}
