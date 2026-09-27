using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using pViewer.Core.Sources;
using pViewer.Services;

namespace pViewer.Views;

public partial class BatchDialog : Window
{
    private static readonly (BatchOperation Op, string Label)[] Operations =
    [
        (BatchOperation.Resize, "Ridimensiona"),
        (BatchOperation.RotateRight, "Ruota a destra"),
        (BatchOperation.RotateLeft, "Ruota a sinistra"),
        (BatchOperation.FlipHorizontal, "Rifletti orizzontalmente"),
        (BatchOperation.FlipVertical, "Rifletti verticalmente"),
        (BatchOperation.Convert, "Converti formato"),
        (BatchOperation.Grayscale, "Scala di grigi"),
        (BatchOperation.Invert, "Negativo"),
        (BatchOperation.BlackBorder, "Aggiungi bordo nero"),
        (BatchOperation.WhiteBorder, "Aggiungi bordo bianco"),
    ];

    private readonly IImageSource _source;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;
    private bool _outputEdited;
    private bool _settingOutput;

    public BatchDialog(IImageSource source, AppSettings settings)
    {
        _source = source;
        _settings = settings;
        InitializeComponent();
        string kind = source.IsArchive ? "archivio" : "cartella";
        SourceText.Text = $"Origine ({kind}): {source.Location}\n{source.Pages.Count} immagini";
        foreach (var (_, label) in Operations) OperationBox.Items.Add(label);
        BorderBox.Text = settings.BorderThickness.ToString();
        OperationBox.SelectedIndex = 0;
        Closing += Window_Closing;
    }

    private BatchOperation SelectedOperation => Operations[Math.Max(0, OperationBox.SelectedIndex)].Op;

    private string DefaultOutput(BatchOperation op)
    {
        string name = BatchProcessor.DefaultFolderName(op);
        if (_source.IsArchive)
        {
            string dir = Path.GetDirectoryName(_source.Location)!;
            return Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(_source.Location)} - {name}");
        }
        return Path.Combine(_source.Location, name);
    }

    private void OperationBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var op = SelectedOperation;
        ResizePanel.Visibility = op == BatchOperation.Resize ? Visibility.Visible : Visibility.Collapsed;
        ConvertPanel.Visibility = op == BatchOperation.Convert ? Visibility.Visible : Visibility.Collapsed;
        BorderPanel.Visibility = op is BatchOperation.BlackBorder or BatchOperation.WhiteBorder ? Visibility.Visible : Visibility.Collapsed;
        if (!_outputEdited)
        {
            _settingOutput = true;
            OutputBox.Text = DefaultOutput(op);
            _settingOutput = false;
        }
    }

    private void OutputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingOutput) _outputEdited = true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Cartella di destinazione" };
        string? parent = Path.GetDirectoryName(OutputBox.Text);
        if (parent is not null && Directory.Exists(parent)) dlg.InitialDirectory = parent;
        if (dlg.ShowDialog(this) == true) OutputBox.Text = dlg.FolderName;
    }

    private BatchJob? BuildJob(out string? error)
    {
        error = null;
        string output = OutputBox.Text.Trim();
        if (output.Length == 0 || !Path.IsPathFullyQualified(output))
        {
            error = "Indica una cartella di destinazione completa (es. C:\\Foto\\Ridotte).";
            return null;
        }
        var op = SelectedOperation;
        int percent = 0, maxW = 0, maxH = 0, border = _settings.BorderThickness;
        if (op == BatchOperation.Resize)
        {
            if (Pct25.IsChecked == true) percent = 25;
            else if (Pct50.IsChecked == true) percent = 50;
            else if (Pct75.IsChecked == true) percent = 75;
            else if (!int.TryParse(MaxWidthBox.Text, out maxW) || !int.TryParse(MaxHeightBox.Text, out maxH) || maxW < 1 || maxH < 1)
            {
                error = "Larghezza e altezza massime devono essere numeri positivi.";
                return null;
            }
        }
        if (op is BatchOperation.BlackBorder or BatchOperation.WhiteBorder &&
            (!int.TryParse(BorderBox.Text, out border) || border < 1 || border > 2000))
        {
            error = "Spessore del bordo: da 1 a 2000 pixel.";
            return null;
        }
        string? ext = op == BatchOperation.Convert ? (string)((ComboBoxItem)FormatBox.SelectedItem).Tag : null;
        return new BatchJob(op, output, percent, maxW, maxH, ext, _settings.JpegQuality, border, _settings.AutoRotateExif);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var job = BuildJob(out string? error);
        if (job is null)
        {
            StatusText.Text = error;
            return;
        }

        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        OpenFolderButton.Visibility = Visibility.Collapsed;
        CloseButton.Content = "Interrompi";
        ProgressBar.Maximum = _source.Pages.Count;
        ProgressBar.Value = 0;
        var progress = new Progress<BatchProgress>(p =>
        {
            ProgressBar.Value = p.Done;
            StatusText.Text = p.Current.Length > 0 ? $"{p.Done + 1} di {p.Total}: {p.Current}" : "";
        });

        try
        {
            var result = await BatchProcessor.RunAsync(_source, job, progress, _cts.Token);
            var parts = new List<string> { $"{result.Written} immagini salvate" };
            if (result.Skipped > 0) parts.Add($"{result.Skipped} saltate perché già presenti");
            if (result.Errors.Count > 0) parts.Add($"{result.Errors.Count} errori");
            string text = (result.Cancelled ? "Interrotto: " : "Fatto: ") + string.Join(", ", parts) + ".";
            if (result.Errors.Count > 0) text += "\n" + string.Join("\n", result.Errors.Take(5));
            StatusText.Text = text;
            if (result.Written > 0) OpenFolderButton.Visibility = Visibility.Visible;
            _lastOutput = job.OutputFolder;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Errore: {ex.Message}";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            StartButton.IsEnabled = true;
            CloseButton.Content = "Chiudi";
        }
    }

    private string? _lastOutput;

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutput is not null && Directory.Exists(_lastOutput))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_lastOutput}\"") { UseShellExecute = false });
    }

    // Il pulsante ha IsCancel: la chiusura la fa WPF, qui basta interrompere un lavoro in corso.
    private void Close_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // Durante l'elaborazione "Chiudi"/Esc interrompe invece di chiudere.
        if (_cts is not null)
        {
            _cts.Cancel();
            e.Cancel = true;
        }
    }
}
