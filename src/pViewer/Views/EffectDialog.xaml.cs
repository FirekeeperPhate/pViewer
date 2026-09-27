using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using pViewer.Imaging;
using pViewer.ViewModels;

namespace pViewer.Views;

/// <summary>Generic effects dialog: sliders and a live preview on a reduced copy.</summary>
public partial class EffectDialog : Window
{
    private readonly EffectDefinition _effect;
    private readonly BitmapSource _preview;
    private readonly double _scale;
    private readonly List<Slider> _sliders = [];
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private BitmapSource? _result;
    private int _request;

    public EffectDialog(EffectDefinition effect, BitmapSource source)
    {
        InitializeComponent();
        Title = effect.Title;
        _effect = effect;
        _preview = ImageOps.FitWithin(source, 580, 400);
        _scale = (double)_preview.PixelWidth / source.PixelWidth;
        PreviewImage.Source = _preview;

        for (int i = 0; i < effect.Sliders.Count; i++)
        {
            var def = effect.Sliders[i];
            SlidersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = def.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 16, 4) };
            var slider = new Slider
            {
                Minimum = def.Min, Maximum = def.Max, Value = def.Default,
                SmallChange = def.Step, LargeChange = def.Step * 10,
                TickFrequency = def.Step, IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            void UpdateLabel() => value.Text = (def.Step < 1 ? slider.Value.ToString("0.0") : slider.Value.ToString("0")) + def.Unit;
            UpdateLabel();
            slider.ValueChanged += (_, _) =>
            {
                UpdateLabel();
                _debounce.Stop();
                _debounce.Start();
            };
            Grid.SetRow(label, i);
            Grid.SetRow(slider, i);
            Grid.SetColumn(slider, 1);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 2);
            SlidersGrid.Children.Add(label);
            SlidersGrid.Children.Add(slider);
            SlidersGrid.Children.Add(value);
            _sliders.Add(slider);
        }

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RecomputeAsync();
        };
        Loaded += (_, _) => _ = RecomputeAsync();
    }

    public double[] Values => _sliders.Select(s => s.Value).ToArray();

    private async Task RecomputeAsync()
    {
        int id = ++_request;
        var values = Values;
        Busy.Visibility = Visibility.Visible;
        try
        {
            var result = await Task.Run(() => _effect.Apply(_preview, values, _scale));
            if (id != _request) return;
            _result = result;
            if (CompareBox.IsChecked != true) PreviewImage.Source = result;
        }
        catch (Exception)
        {
            // Preview failed: keep the previous one.
        }
        finally
        {
            if (id == _request) Busy.Visibility = Visibility.Collapsed;
        }
    }

    private void CompareBox_Click(object sender, RoutedEventArgs e) =>
        PreviewImage.Source = CompareBox.IsChecked == true ? _preview : _result ?? _preview;

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        for (int i = 0; i < _sliders.Count; i++) _sliders[i].Value = _effect.Sliders[i].Default;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
