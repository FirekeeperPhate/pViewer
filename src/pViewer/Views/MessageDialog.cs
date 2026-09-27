using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace pViewer.Views;

/// <summary>
/// Replacement for MessageBox that follows the app theme (the Win32 MessageBox is always light).
/// Like MessageBox it answers to plain Y / N / Esc key presses.
/// </summary>
public sealed class MessageDialog : Window
{
    private MessageBoxResult _result;
    private readonly MessageBoxButton _buttons;

    private MessageDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        _buttons = buttons;
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _result = CancelResult;

        // Theme brushes: fixed colors picked for the dark theme are too pale on the light one.
        var (glyph, brushKey) = icon switch
        {
            MessageBoxImage.Error => ("", "SystemFillColorCriticalBrush"),
            MessageBoxImage.Warning => ("", "SystemFillColorCautionBrush"),
            MessageBoxImage.Question => ("", "AccentTextFillColorPrimaryBrush"),
            MessageBoxImage.Information => ("", "AccentTextFillColorPrimaryBrush"),
            _ => ("", ""),
        };

        var body = new DockPanel { Margin = new Thickness(24, 20, 24, 8) };
        if (glyph.Length > 0)
        {
            var iconText = new TextBlock
            {
                Text = glyph,
                FontSize = 30,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 18, 0),
            };
            iconText.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            iconText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            DockPanel.SetDock(iconText, Dock.Left);
            body.Children.Add(iconText);
        }
        body.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
        });

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var labels = buttons switch
        {
            MessageBoxButton.OKCancel => new[] { ("OK", MessageBoxResult.OK), ("Cancel", MessageBoxResult.Cancel) },
            MessageBoxButton.YesNo => [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No)],
            MessageBoxButton.YesNoCancel => [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel)],
            _ => [("OK", MessageBoxResult.OK)],
        };
        for (int i = 0; i < labels.Length; i++)
        {
            var (label, result) = labels[i];
            var button = new Button
            {
                Content = label,
                MinWidth = 96,
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = i == 0,
                IsCancel = result == CancelResult && labels.Length > 1,
            };
            if (i == 0) button.SetResourceReference(StyleProperty, "AccentButtonStyle");
            button.Click += (_, _) => Close(result);
            buttonRow.Children.Add(button);
        }

        var footer = new Border
        {
            Padding = new Thickness(24, 14, 24, 14),
            Margin = new Thickness(0, 16, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80)),
            Child = buttonRow,
        };

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => PlaySound(icon);
    }

    /// <summary>Result used for Esc and for the title bar close button.</summary>
    private MessageBoxResult CancelResult => _buttons switch
    {
        MessageBoxButton.YesNo => MessageBoxResult.No,
        MessageBoxButton.OK => MessageBoxResult.OK,
        _ => MessageBoxResult.Cancel,
    };

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool yesNo = _buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel;
        if (yesNo && e.Key == Key.Y) { Close(MessageBoxResult.Yes); e.Handled = true; }
        else if (yesNo && e.Key == Key.N) { Close(MessageBoxResult.No); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(CancelResult); e.Handled = true; }
    }

    private void Close(MessageBoxResult result)
    {
        _result = result;
        DialogResult = true;
    }

    private static void PlaySound(MessageBoxImage icon)
    {
        switch (icon)
        {
            case MessageBoxImage.Error: SystemSounds.Hand.Play(); break;
            case MessageBoxImage.Warning: SystemSounds.Exclamation.Play(); break;
            case MessageBoxImage.Information: SystemSounds.Asterisk.Play(); break;
        }
    }

    public static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
    {
        var dialog = new MessageDialog(message, title, buttons, icon);
        if (owner is { IsLoaded: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog._result;
    }
}
