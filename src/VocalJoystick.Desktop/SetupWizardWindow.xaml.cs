using System.Globalization;
using System.Windows;
using VocalJoystick.Audio;

namespace VocalJoystick.Desktop;

public partial class SetupWizardWindow : Window
{
    private WindowsMicrophoneSource? _source;
    private CancellationTokenSource? _cancellation;
    private Task? _levelTask;

    public SetupWizardWindow(IEnumerable<MicrophoneOption> devices, int? selectedDeviceIndex, float scale)
    {
        InitializeComponent();
        MicrophoneBox.ItemsSource = devices.ToArray();
        MicrophoneBox.SelectedItem = MicrophoneBox.Items.Cast<MicrophoneOption>()
            .FirstOrDefault(device => device.Index == selectedDeviceIndex) ?? MicrophoneBox.Items.Cast<MicrophoneOption>().FirstOrDefault();
        ScaleBox.Text = scale.ToString(CultureInfo.InvariantCulture);
    }

    public int? SelectedMicrophoneIndex => (MicrophoneBox.SelectedItem as MicrophoneOption)?.Index;
    public float PixelsPerVelocityUnit { get; private set; }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedMicrophoneIndex;
        MicrophoneBox.ItemsSource = WindowsMicrophoneSource.GetDeviceNames()
            .Select((name, index) => new MicrophoneOption(index, name)).ToArray();
        MicrophoneBox.SelectedItem = MicrophoneBox.Items.Cast<MicrophoneOption>()
            .FirstOrDefault(device => device.Index == selected) ?? MicrophoneBox.Items.Cast<MicrophoneOption>().FirstOrDefault();
    }

    private async void LevelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_levelTask is not null)
        {
            await StopLevelAsync();
            return;
        }
        if (MicrophoneBox.SelectedItem is not MicrophoneOption device)
        {
            LevelText.Text = "Выберите микрофон.";
            return;
        }

        try
        {
            _source = new WindowsMicrophoneSource(device.Index);
            _cancellation = new CancellationTokenSource();
            LevelButton.Content = "Остановить проверку";
            LevelText.Text = "Слушаю…";
            _levelTask = ReadLevelAsync(_source, _cancellation.Token);
        }
        catch (Exception error)
        {
            LevelText.Text = $"Не удалось открыть микрофон: {error.Message}";
            await StopLevelAsync();
        }
    }

    private async Task ReadLevelAsync(WindowsMicrophoneSource source, CancellationToken cancellationToken)
    {
        try
        {
            await source.StartAsync(cancellationToken);
            await foreach (var chunk in source.ReadAllAsync(cancellationToken))
            {
                var rms = MathF.Sqrt(chunk.Samples.Select(sample => sample * sample).Average());
                await Dispatcher.InvokeAsync(() =>
                {
                    LevelBar.Value = Math.Min(LevelBar.Maximum, rms);
                    LevelText.Text = $"RMS: {rms:F0}";
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            await Dispatcher.InvokeAsync(() => LevelText.Text = $"Ошибка уровня: {error.Message}");
        }
        finally
        {
            await source.DisposeAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_source, source))
                {
                    _source = null;
                    _levelTask = null;
                    LevelButton.Content = "Начать проверку уровня";
                    if (!LevelText.Text.StartsWith("Ошибка уровня", StringComparison.Ordinal)) LevelText.Text = "Остановлено";
                }
            });
        }
    }

    private async Task StopLevelAsync()
    {
        var source = _source;
        if (source is not null) await source.StopAsync();
        _cancellation?.Cancel();
        if (_levelTask is not null) await _levelTask;
        _cancellation?.Dispose();
        _cancellation = null;
        _source = null;
        _levelTask = null;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!float.TryParse(ScaleBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) ||
            !float.IsFinite(scale) || scale <= 0f)
        {
            LevelText.Text = "Масштаб должен быть положительным числом.";
            return;
        }
        PixelsPerVelocityUnit = scale;
        await StopLevelAsync();
        DialogResult = true;
    }

    protected override async void OnClosed(EventArgs e)
    {
        await StopLevelAsync();
        base.OnClosed(e);
    }
}
