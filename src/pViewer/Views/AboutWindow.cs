using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pViewer.Services;

namespace pViewer.Views;

public sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = "About pViewer";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/pv.ico")),
            Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Left,
        });
        panel.Children.Add(new TextBlock { Text = "pViewer", FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(new TextBlock { Text = $"Version {version?.ToString(3)}", Opacity = 0.7 });
        panel.Children.Add(new TextBlock
        {
            Text = "Image viewer and small editor, with comic and manga reading straight from archives.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Copyright © 2013-2026 Phate. Released under the GNU GPL v3.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Opacity = 0.8,
        });

        var credits = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0), Opacity = 0.7, FontSize = 12 };
        credits.Inlines.Add(new Run("Uses: "));
        AddLink(credits, "ImageSharp", "https://github.com/SixLabors/ImageSharp");
        credits.Inlines.Add(new Run(" (Six Labors Split License), "));
        AddLink(credits, "SharpCompress", "https://github.com/adamhathcock/sharpcompress");
        credits.Inlines.Add(new Run(" (MIT), "));
        AddLink(credits, "MetadataExtractor", "https://github.com/drewnoakes/metadata-extractor-dotnet");
        credits.Inlines.Add(new Run(" (Apache 2.0), "));
        AddLink(credits, "CommunityToolkit.Mvvm", "https://github.com/CommunityToolkit/dotnet");
        credits.Inlines.Add(new Run(" (MIT)."));
        panel.Children.Add(credits);

        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        panel.Children.Add(ok);
        Content = panel;
    }

    private static void AddLink(TextBlock target, string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url) };
        link.RequestNavigate += (_, e) => { Shell.OpenUrl(e.Uri.AbsoluteUri); e.Handled = true; };
        target.Inlines.Add(link);
    }
}
