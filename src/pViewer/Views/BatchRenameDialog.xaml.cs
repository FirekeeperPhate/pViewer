using System.Windows;
using System.Windows.Controls;
using pViewer.Services;
using pViewer.ViewModels;

namespace pViewer.Views;

public partial class BatchRenameDialog : Window
{
    private readonly int _count;
    private bool _ready;

    public BatchRenameDialog(string suggestedBase, int count)
    {
        _count = count;
        InitializeComponent();
        IntroText.Text = $"I {count} file della cartella verranno rinominati nell'ordine in cui li vedi. " +
                         "L'estensione resta quella originale.";
        BaseBox.Text = suggestedBase;
        DigitsBox.Text = Math.Max(2, count.ToString().Length).ToString();
        _ready = true;
        UpdatePreview();
        Loaded += (_, _) => { BaseBox.Focus(); BaseBox.SelectAll(); };
    }

    public BatchRenameOptions? Options { get; private set; }

    private BatchRenameOptions? Read(out string? error)
    {
        error = null;
        string name = BaseBox.Text.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) error = "Nome base non valido.";
        else if (!int.TryParse(StartBox.Text, out int start) || start < 0) error = "Il primo numero deve essere 0 o più.";
        else if (!int.TryParse(DigitsBox.Text, out int digits) || digits < 1 || digits > 9) error = "Cifre: da 1 a 9.";
        else return new BatchRenameOptions(name, start, digits);
        return null;
    }

    private void Input_Changed(object sender, TextChangedEventArgs e)
    {
        if (_ready) UpdatePreview();
    }

    private void UpdatePreview()
    {
        var o = Read(out string? error);
        ErrorText.Text = error;
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = o is not null;
        if (o is null) { PreviewText.Text = ""; return; }
        var lines = new List<string> { BatchRenamer.TargetName(o, 0, ".jpg") };
        if (_count > 1) lines.Add(BatchRenamer.TargetName(o, 1, ".jpg"));
        if (_count > 3) lines.Add("…");
        if (_count > 2) lines.Add(BatchRenamer.TargetName(o, _count - 1, ".jpg"));
        PreviewText.Text = string.Join("\n", lines);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Options = Read(out _);
        if (Options is not null) DialogResult = true;
    }
}
