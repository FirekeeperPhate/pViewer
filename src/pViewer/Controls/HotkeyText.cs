using System.Windows;
using System.Windows.Controls;
using pViewer.Services;

namespace pViewer.Controls;

/// <summary>
/// Ties a menu item or a toolbar button to a keyboard command, so the keys it shows follow the
/// user's shortcuts: menu items get them as InputGestureText, buttons as "Label (keys)" tooltip.
/// </summary>
public static class HotkeyText
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(string), typeof(HotkeyText));

    public static string? GetCommand(DependencyObject d) => (string?)d.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject d, string? value) => d.SetValue(CommandProperty, value);

    /// <summary>Tooltip text before the keys (buttons only).</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.RegisterAttached(
        "Label", typeof(string), typeof(HotkeyText));

    public static string? GetLabel(DependencyObject d) => (string?)d.GetValue(LabelProperty);
    public static void SetLabel(DependencyObject d, string? value) => d.SetValue(LabelProperty, value);

    /// <summary>Updates every tagged element under <paramref name="root"/> (logical tree).</summary>
    public static void Apply(DependencyObject root, IReadOnlyDictionary<string, List<string>>? overrides)
    {
        if (GetCommand(root) is { } id)
        {
            if (root is MenuItem item) item.InputGestureText = Hotkeys.DisplayText(id, overrides);
            else if (root is FrameworkElement element && GetLabel(root) is { } label)
            {
                string keys = Hotkeys.DisplayText(id, overrides, max: 2);
                element.ToolTip = keys.Length == 0 ? label : $"{label} ({keys})";
            }
        }
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d) Apply(d, overrides);
    }
}
