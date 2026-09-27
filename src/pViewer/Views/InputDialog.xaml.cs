using System.Windows;

namespace pViewer.Views;

public partial class InputDialog : Window
{
    private readonly Func<string, string?>? _validate;

    public InputDialog(string title, string prompt, string initial, Func<string, string?>? validate)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initial;
        _validate = validate;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string? error = _validate?.Invoke(ValueBox.Text);
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            ValueBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
