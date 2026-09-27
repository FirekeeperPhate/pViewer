using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using pViewer.ViewModels;

namespace pViewer.Views;

public partial class MetadataWindow : Window
{
    public sealed record Row(string Group, string Name, string Value);

    private readonly List<Row> _rows;
    private readonly ICollectionView _view;

    public MetadataWindow(string title, IReadOnlyList<MetadataGroup> groups)
    {
        InitializeComponent();
        Title = $"Metadata — {title}";
        _rows = groups.SelectMany(g => g.Tags.Select(t => new Row(g.Name, t.Key, t.Value))).ToList();
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.Group)));
        List.ItemsSource = _view;
        Loaded += (_, _) => FilterBox.Focus();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string f = FilterBox.Text.Trim();
        _view.Filter = f.Length == 0 ? null : o => o is Row r &&
            (r.Name.Contains(f, StringComparison.CurrentCultureIgnoreCase) || r.Value.Contains(f, StringComparison.CurrentCultureIgnoreCase));
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var group in _rows.GroupBy(r => r.Group))
        {
            sb.AppendLine($"[{group.Key}]");
            foreach (var r in group) sb.AppendLine($"{r.Name}: {r.Value}");
            sb.AppendLine();
        }
        try { Clipboard.SetText(sb.ToString()); }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard busy with another program */ }
    }
}
