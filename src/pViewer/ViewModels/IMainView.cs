using System.Windows;
using System.Windows.Media.Imaging;
using pViewer.Controls;
using pViewer.Core.Sources;
using pViewer.Imaging;
using pViewer.Services;

namespace pViewer.ViewModels;

public sealed record EffectSlider(string Label, double Min, double Max, double Default, double Step = 1, string Unit = "");

/// <summary>Effetto con anteprima. <c>scale</c> è il rapporto anteprima/originale (per raggi e sigma).</summary>
public sealed record EffectDefinition(
    string Title, IReadOnlyList<EffectSlider> Sliders, Func<BitmapSource, double[], double, BitmapSource> Apply);

public sealed record BatchRenameOptions(string BaseName, int Start, int Digits);

public sealed record MetadataGroup(string Name, IReadOnlyList<KeyValuePair<string, string>> Tags);

/// <summary>Ciò che il ViewModel chiede alla finestra (dialoghi e visualizzatore).</summary>
public interface IMainView
{
    void ShowPages(IReadOnlyList<BitmapSource> pages, bool preserveView, IReadOnlyList<ImageAnimation?>? animations = null);
    void ShowPreview(BitmapSource thumbnail, int fullWidth, int fullHeight);
    void ClearPages();

    MessageBoxResult Ask(string message, string title, MessageBoxButton buttons, MessageBoxImage icon = MessageBoxImage.Question);
    void ShowError(string message);

    string? PickOpenFile(string? initialDirectory);
    string? PickSaveFile(string? initialDirectory, string fileName);
    string? AskText(string title, string prompt, string initial, Func<string, string?>? validate = null);

    double[]? ShowEffectDialog(EffectDefinition effect, BitmapSource source);
    (int Width, int Height)? ShowResizeDialog(int width, int height);
    void ShowBatchDialog(IImageSource source, AppSettings settings);
    BatchRenameOptions? ShowBatchRenameDialog(string suggestedBase, int count);
    bool ShowSettingsDialog(AppSettings settings);
    void ShowMetadata(string title, IReadOnlyList<MetadataGroup> groups);
    TextStyle? ShowTextStyleDialog(TextStyle current);

    bool IsFullscreen { get; }
    void SetFullscreen(bool fullscreen);
    IntPtr WindowHandle { get; }
}
