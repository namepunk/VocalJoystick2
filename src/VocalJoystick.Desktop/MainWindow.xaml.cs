using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VocalJoystick.Audio;
using VocalJoystick.Commands;
using VocalJoystick.Core;
using VocalJoystick.Features;
using VocalJoystick.Input;
using VocalJoystick.Models;
using VocalJoystick.Motion;

namespace VocalJoystick.Desktop;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cancellation;
    private WindowsMicrophoneSource? _source;
    private Task? _captureTask;
    private VoiceCursorCoordinator? _coordinator;
    private int _resultCount;
    private int _voiceActivityResultCount;
    private readonly object _motionTrainingLock = new();
    private readonly object _commandTrainingLock = new();
    private readonly CommandTrainingStore _commandTrainingStore;
    private FeatureNormalizer? _commandNormalizer;
    private readonly List<AudioChunk> _commandAudioChunks = [];
    private List<float[]>? _commandTrainingFrames;
    private int? _collectingCommandClass;
    private long _commandSpeechStartSample;
    private long _commandSpeechEndSample;
    private DateTime _commandCollectionStarted;
    private readonly List<List<float[][]>> _motionExamples = Enumerable.Range(0, UserMotionClassifier.RequiredClassCount)
        .Select(_ => new List<float[][]>()).ToList();
    private List<float[]>? _collectingMotionFrames;
    private List<float[]>? _collectingMotionAudioChunks;
    private int? _collectingMotionClass;
    private DateTime _motionCollectionStarted;
    private volatile bool _isSpeechActive;
    private bool _cursorControlEnabled;
    private string _activeCommandRecognizerName = "не запущен";
    private string _activeMicrophoneDescription = "микрофон не выбран";
    private readonly DesktopSettingsStore _settingsStore = new();
    private int? _preferredMicrophoneIndex;

    public MainWindow()
    {
        InitializeComponent();
        _commandTrainingStore = new CommandTrainingStore(GetCommandTrainingManifestPath());
        SetDevelopmentProfileDefaults();
        ApplySettings(_settingsStore.Load());
        RefreshDevices();
        UpdateMotionTrainingText(File.Exists(GetUserMotionModelPath()) ? "Найдена сохранённая пользовательская модель." : "Используется базовая модель.");
        UpdateCommandTrainingText("Запись команд не активна.");
    }

    private void SetDevelopmentProfileDefaults()
    {
        var bundledProfile = Path.Combine(AppContext.BaseDirectory, "profiles");
        if (File.Exists(Path.Combine(bundledProfile, "s4.mlrsc")))
        {
            NormsPathBox.Text = Path.Combine(bundledProfile, "vj.norms");
            ModelPathBox.Text = Path.Combine(bundledProfile, "s4.mlrsc");
            ParametersPathBox.Text = Path.Combine(bundledProfile, "prm.cfg");
            PowerPathBox.Text = Path.Combine(bundledProfile, "s4.power");
            RegressionPathBox.Text = Path.Combine(bundledProfile, "vj.lr");
            CommandModelPathBox.Text = Path.Combine(bundledProfile, "d.hmmf");
            CommandDictionaryPathBox.Text = Path.Combine(bundledProfile, "vj.dict");
            return;
        }

        var root = FindProfileRoot();
        var legacyData = Path.Combine(root, "1_VocalJoystick", "SourceDir", "vjlib", "data");
        NormsPathBox.Text = Path.Combine(legacyData, "params", "vj.norms");
        ModelPathBox.Text = Path.Combine(legacyData, "users", "default", "s4.mlrsc");
        ParametersPathBox.Text = Path.Combine(legacyData, "params", "prm.cfg");
        PowerPathBox.Text = Path.Combine(legacyData, "users", "default", "s4.power");
        RegressionPathBox.Text = Path.Combine(legacyData, "params", "vj.lr");
        CommandModelPathBox.Text = Path.Combine(legacyData, "users", "default", "d.hmmf");
        CommandDictionaryPathBox.Text = Path.Combine(legacyData, "dict", "vj.dict");
    }

    private static string FindProfileRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "1_VocalJoystick", "SourceDir", "vjlib", "data")))
                    return directory.FullName;
            }
        }
        return Environment.CurrentDirectory;
    }

    private void RefreshDevices()
    {
        var selected = (MicrophoneBox.SelectedItem as MicrophoneOption)?.Index ?? _preferredMicrophoneIndex;
        MicrophoneBox.ItemsSource = WindowsMicrophoneSource.GetDeviceNames()
            .Select((name, index) => new MicrophoneOption(index, name)).ToArray();
        MicrophoneBox.SelectedItem = MicrophoneBox.Items.Cast<MicrophoneOption>()
            .FirstOrDefault(device => device.Index == selected) ?? MicrophoneBox.Items.Cast<MicrophoneOption>().FirstOrDefault();
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var dialog = new OpenFileDialog { CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var target = tag switch
        {
            "norms" => NormsPathBox,
            "model" => ModelPathBox,
            "parameters" => ParametersPathBox,
            "power" => PowerPathBox,
            "regression" => RegressionPathBox,
            "commandModel" => CommandModelPathBox,
            "commandDictionary" => CommandDictionaryPathBox,
            _ => null
        };
        if (target is not null) target.Text = dialog.FileName;
    }

    private async void StartDiagnostics_Click(object sender, RoutedEventArgs e) => await StartAsync(enableCursor: false);

    private async void EnableCursor_Click(object sender, RoutedEventArgs e) => await StartAsync(enableCursor: true);

    private void SetupWizard_Click(object sender, RoutedEventArgs e)
    {
        if (_captureTask is not null) return;
        var scale = ReadPixelScaleOrDefault();
        var devices = MicrophoneBox.Items.Cast<MicrophoneOption>().ToArray();
        var wizard = new SetupWizardWindow(devices, (MicrophoneBox.SelectedItem as MicrophoneOption)?.Index, scale) { Owner = this };
        if (wizard.ShowDialog() != true) return;

        _preferredMicrophoneIndex = wizard.SelectedMicrophoneIndex;
        PixelScaleBox.Text = wizard.PixelsPerVelocityUnit.ToString(CultureInfo.InvariantCulture);
        RefreshDevices();
        SaveSettings();
        SetStatus("Первичная настройка сохранена. Начните диагностику перед включением курсора.");
    }

    private async Task StartAsync(bool enableCursor)
    {
        await StopAsync();
        if (MicrophoneBox.SelectedItem is not MicrophoneOption device)
        {
            SetStatus("Выберите микрофон.");
            return;
        }
        if (!float.TryParse(PixelScaleBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) ||
            !float.IsFinite(scale) || scale <= 0f)
        {
            SetStatus("Масштаб должен быть положительным числом, например 0.1.");
            return;
        }
        if (!float.TryParse(CommandThresholdMultiplierBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var commandThresholdMultiplier) ||
            !float.IsFinite(commandThresholdMultiplier) ||
            commandThresholdMultiplier < UserCommandSequenceModel.MinimumThresholdMultiplier ||
            commandThresholdMultiplier > UserCommandSequenceModel.MaximumThresholdMultiplier)
        {
            SetStatus($"Множитель порога команды должен быть от {UserCommandSequenceModel.MinimumThresholdMultiplier} до {UserCommandSequenceModel.MaximumThresholdMultiplier}.");
            return;
        }
        var paths = new[] { NormsPathBox.Text, ModelPathBox.Text, ParametersPathBox.Text,
            PowerPathBox.Text, RegressionPathBox.Text };
        if (paths.Any(path => string.IsNullOrWhiteSpace(path) || !File.Exists(path)))
        {
            SetStatus("Укажите существующие файлы нормализации, модели и legacy-профиля.");
            return;
        }

        try
        {
            var userModelPath = GetUserMotionModelPath();
            var classifier = File.Exists(userModelPath)
                ? UserMotionClassifier.Load(userModelPath)
                : LoadClassifier(ModelPathBox.Text);
            var motion = LegacyMotionControllerFactory.Create(ParametersPathBox.Text, PowerPathBox.Text, RegressionPathBox.Text, classifier.ClassCount);
            var commandUserModelPath = GetUserCommandModelPath();
            UserCommandSequenceModel? trainedCommandModel = File.Exists(commandUserModelPath)
                ? UserCommandSequenceModel.Load(commandUserModelPath)
                : _commandTrainingStore.Counts.Values.All(count => count >= CommandTrainingStore.MinimumExamplesPerClass)
                    ? _commandTrainingStore.Train()
                    : null;
            LegacyDiscreteCommandRecognizer? commandRecognizer = null;
            if (trainedCommandModel is null)
            {
                if (!File.Exists(CommandModelPathBox.Text) || !File.Exists(CommandDictionaryPathBox.Text))
                    throw new FileNotFoundException("Укажите legacy-модель и словарь команд либо обучите пользовательскую модель.");
                commandRecognizer = LegacyDiscreteCommandRecognizer.Load(
                    CommandModelPathBox.Text, CommandDictionaryPathBox.Text, ParametersPathBox.Text);
            }
            _activeCommandRecognizerName = trainedCommandModel is null
                ? "legacy HMM/BMM"
                : "пользовательская sequence-модель (DTW)";
            _activeMicrophoneDescription = device.Name;
            AudioDiagnosticsText.Text = $"Захват запускается. Микрофон: {_activeMicrophoneDescription}. Режим: {(enableCursor ? "управление включено" : "диагностика, ввод заблокирован")}.";
            DirectionDiagnosticsText.Text = "Ожидание первого классифицированного кадра направления.";
            ClickDiagnosticsText.Text = $"Ожидание речи. Распознаватель: {_activeCommandRecognizerName}; " +
                $"порог-множитель={commandThresholdMultiplier:F2}; вывод {(enableCursor ? "разрешён" : "заблокирован диагностическим режимом")}.";
            var normalizer = FeatureNormalizer.Load(NormsPathBox.Text);
            _commandNormalizer = normalizer;
            _source = new WindowsMicrophoneSource(device.Index);
            var runner = new AudioRecognitionRunner(
                _source,
                new LegacyRecognitionPipeline(normalizer, classifier),
                LegacyVoiceActivityConfiguration.Load(ParametersPathBox.Text), commandRecognizer,
                trainedCommandModel is null ? null : new UserCommandSequenceRecognizer(trainedCommandModel, commandThresholdMultiplier),
                trainedCommandModel is null ? null : normalizer);
            var gate = new CursorSafetyGate();
            _coordinator = new VoiceCursorCoordinator(motion, new MotionDeltaQuantizer(scale), gate,
                enableCursor ? new WindowsCursorOutput() : new DisabledCursorOutput(),
                enableCursor ? new WindowsMouseActionOutput() : new DisabledMouseActionOutput());
            runner.RecognitionWithEnergyProduced += RecognitionProduced;
            runner.AudioChunkProduced += AudioChunkProduced;
            runner.VoiceActivityProduced += VoiceActivityProduced;
            runner.DiscreteCommandProduced += DiscreteCommandProduced;
            runner.CommandCandidateProduced += CommandCandidateProduced;
            runner.LegacyCommandCandidateProduced += LegacyCommandCandidateProduced;
            runner.CommandFeatureProduced += CommandFeatureProduced;
            _cancellation = new CancellationTokenSource();
            _resultCount = 0;
            _voiceActivityResultCount = 0;
            AudioDiagnosticsText.Text = $"Микрофон: {_activeMicrophoneDescription}; состояние: тишина, ожидание аудио; " +
                $"формат источника: 16 kHz, mono, PCM16. sample=0.";
            if (enableCursor) gate.Enable();
            _cursorControlEnabled = enableCursor;
            SaveSettings();
            SetRunningState(true);
            SetStatus(enableCursor
                ? "Управление курсором включено. Пауза или Остановить немедленно закрывают выход."
                : "Диагностика активна. Курсор не перемещается.");
            _captureTask = CaptureAsync(runner, _source, _cancellation.Token);
        }
        catch (Exception error)
        {
            await StopAsync();
            SetStatus($"Не удалось начать: {error.Message}");
        }
    }

    private async Task CaptureAsync(AudioRecognitionRunner runner, WindowsMicrophoneSource source, CancellationToken cancellationToken)
    {
        try
        {
            await runner.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                SetStatus($"Захват остановлен с ошибкой: {error.Message}");
                AudioDiagnosticsText.Text = $"ОШИБКА аудиозахвата: {error.GetType().Name}: {error.Message}";
                ClickDiagnosticsText.Text = $"Обработка команд остановлена из-за ошибки захвата: {error.Message}";
            });
        }
        finally
        {
            await source.DisposeAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_source, source))
                {
                    _source = null;
                    _coordinator = null;
                    SetRunningState(false);
                    if (!StatusText.Text.StartsWith("Захват остановлен с ошибкой", StringComparison.Ordinal)) SetStatus("Остановлено.");
                }
            });
        }
    }

    private void RecognitionProduced(object? sender, RecognitionWithEnergyResult result)
    {
        CollectMotionTrainingFrame(result);
        var cursor = _coordinator?.Process(result);
        if (cursor is null) return;
        var cursorResult = cursor.Value;
        _resultCount++;
        if (_resultCount % 5 != 0) return;
        var best = Array.IndexOf(result.Scores, result.Scores.Max());
        Dispatcher.BeginInvoke(() => DirectionDiagnosticsText.Text =
            $"Состояние: классификация активна; sample={result.StartSample}; best-class={best}; " +
            $"scores=[{string.Join(", ", result.Scores.Select(score => score.ToString("F4", CultureInfo.InvariantCulture)))}]; " +
            $"direction=({cursorResult.Motion.XDirection:F3}, {cursorResult.Motion.YDirection:F3}); " +
            $"velocity={cursorResult.Motion.Velocity:F3}; delta=({cursorResult.Delta.X}, {cursorResult.Delta.Y}); " +
            $"курсор={(cursorResult.AppliedToCursor ? "двигался" : "не двигался")}");
    }

    private void VoiceActivityProduced(object? sender, LegacyVoiceActivityResult result)
    {
        _isSpeechActive = result.IsSpeech;
        _voiceActivityResultCount++;
        if (!result.StartedSpeech && !result.StartedSilence && _voiceActivityResultCount % 5 != 0) return;
        var audioText = $"Микрофон: {_activeMicrophoneDescription}; формат: 16 kHz, mono, PCM16; " +
            $"VAD={(result.IsSpeech ? "речь" : "тишина")}; power={result.SmoothedPower:F1}; " +
            $"noise={result.NoisePower:F1}; RMS={result.RootMeanSquareEnergy:F1}; " +
            $"ZCR={result.ZeroCrossings}; sample={result.StartSample}.";
        Dispatcher.BeginInvoke(() => AudioDiagnosticsText.Text = audioText);

        if (result.StartedSpeech)
        {
            var text = $"Обнаружен речевой сегмент (sample={result.StartSample}); распознаватель: {_activeCommandRecognizerName}; ожидаю конец фразы.";
            Dispatcher.BeginInvoke(() => ClickDiagnosticsText.Text = text);
        }
        else if (result.StartedSilence)
        {
            Dispatcher.BeginInvoke(() => ClickDiagnosticsText.Text =
                $"Речевой сегмент завершён (sample={result.StartSample}); выполняется классификация {_activeCommandRecognizerName}.");
        }
    }

    private void DiscreteCommandProduced(object? sender, DiscreteCommandProducedEventArgs result)
    {
        try
        {
            var coordinator = _coordinator;
            if (coordinator is null)
            {
                Dispatcher.BeginInvoke(() => ClickDiagnosticsText.Text =
                    $"Команда «{result.Label}» распознана, но coordinator отсутствует; действие не отправлено.");
                return;
            }

            var action = result.ActionId is { } actionId
                ? coordinator.ProcessCommandAction(actionId)
                : coordinator.ProcessDiscreteCommand(result.CommandId);
            var probability = float.IsFinite(result.Probability) ? $"; probability={result.Probability:P0}" : string.Empty;
            var clickState = !action.IsMapped
                ? "действие не сопоставлено"
                : !action.Executed
                    ? $"gate заблокировал вызов (enabled={coordinator.IsInputEnabled})"
                    : $"вызов Windows mouse_event завершился без исключения ({action.Action}); подтверждения фактического события от Windows API нет";
            var text = $"Команда ПРИНЯТА: «{result.Label}»{probability}; actionId={result.ActionId ?? $"legacy-id:{result.CommandId}"}; " +
                $"mapping={action.Action?.ToString() ?? "нет"}; gate={coordinator.IsInputEnabled}; результат: {clickState}.";
            Dispatcher.BeginInvoke(() =>
            {
                ClickDiagnosticsText.Text = text;
                SetStatus(text);
            });
        }
        catch (Exception error)
        {
            var text = $"Команда «{result.Label}» распознана, ОШИБКА отправки действия: {error.GetType().Name}: {error.Message}";
            Dispatcher.BeginInvoke(() =>
            {
                ClickDiagnosticsText.Text = text;
                SetStatus(text);
            });
        }
    }

    private void CommandCandidateProduced(object? sender, CommandCandidateProducedEventArgs result)
    {
        var decision = result.Decision;
        var text = decision.IsAccepted
            ? $"Классификатор ПРИНЯЛ «{decision.Label}»: score={decision.BestDistance:F3}, " +
              $"порог={decision.AcceptanceThreshold:F3}, запас={decision.SecondBestDistance - decision.BestDistance:F3}; " +
              "следующий этап — сопоставление действия и safety gate."
            : $"Классификатор ОТКЛОНИЛ: reason={decision.Rejection}; ближайший={decision.Label ?? "нет"}, " +
              $"score={decision.BestDistance:F3}, порог={decision.AcceptanceThreshold:F3}, second={decision.SecondBestDistance:F3}.";
        Dispatcher.BeginInvoke(() => ClickDiagnosticsText.Text = text);
    }

    private void LegacyCommandCandidateProduced(object? sender, LegacyCommandCandidateProducedEventArgs result)
    {
        var recognition = result.Result;
        string text;
        if (!recognition.Segmentation.IsAccepted)
        {
            var segmentation = recognition.Segmentation;
            text = $"Legacy-сегментация ОТКЛОНИЛА фразу: {segmentation.Rejection}; " +
                $"frames={segmentation.Segment?.Features.Count ?? 0}; sample={result.StartSample}.";
        }
        else if (recognition.Acceptance?.AcceptedScore is { } accepted)
        {
            text = $"Legacy-классификатор ПРИНЯЛ «{accepted.Pronunciation.Orthography}»: " +
                $"probability={accepted.RelativeProbability:P1}, threshold={recognition.Acceptance.ProbabilityThreshold:F3}; " +
                "следующий этап — сопоставление действия и safety gate.";
        }
        else
        {
            var nearest = recognition.RankedScores.FirstOrDefault();
            text = nearest is null
                ? $"Legacy-классификатор не вернул гипотез; sample={result.StartSample}."
                : $"Legacy-классификатор ОТКЛОНИЛ; ближайший={nearest.Pronunciation.Orthography}, " +
                  $"probability={nearest.RelativeProbability:P1}, threshold={recognition.Acceptance?.ProbabilityThreshold:F3}; " +
                  $"sample={result.StartSample}.";
        }
        Dispatcher.BeginInvoke(() => ClickDiagnosticsText.Text = text);
    }

    private void CommandFeatureProduced(object? sender, CommandFeatureFrameEventArgs result)
    {
        int? completedClass = null;
        float[][]? completedFrames = null;
        AudioChunk[]? chunks = null;
        var activity = result.Activity;
        lock (_commandTrainingLock)
        {
            if (_collectingCommandClass is not { } classId) return;
            if (DateTime.UtcNow - _commandCollectionStarted > TimeSpan.FromSeconds(12))
            {
                _collectingCommandClass = null;
                _commandTrainingFrames = null;
                _commandAudioChunks.Clear();
                Dispatcher.BeginInvoke(() => SetStatus("Запись команды отменена: за 12 секунд не обнаружен голосовой фрагмент."));
                return;
            }
            if (activity.StartedSpeech)
            {
                _commandTrainingFrames = [];
                _commandSpeechStartSample = result.StartSample;
            }
            if (_commandTrainingFrames is not null && activity.IsSpeech && _commandNormalizer is { } normalizer)
            {
                var normalized = new float[UserCommandSequenceModel.FeatureCount];
                normalizer.Normalize(result.Features, normalized);
                _commandTrainingFrames.Add(normalized);
            }
            if (!activity.StartedSilence || _commandTrainingFrames is null) return;
            _commandSpeechEndSample = result.StartSample;
            completedClass = classId;
            completedFrames = _commandTrainingFrames.Select(frame => (float[])frame.Clone()).ToArray();
            chunks = _commandAudioChunks.ToArray();
            _collectingCommandClass = null;
            _commandTrainingFrames = null;
            _commandAudioChunks.Clear();
        }

        try
        {
            var frames = completedFrames!;
            if (frames.Length is < UserCommandSequenceModel.MinimumFrameCount or > UserCommandSequenceModel.MaximumFrameCount)
                throw new InvalidOperationException($"Образец содержит {frames.Length} кадров; допустимо {UserCommandSequenceModel.MinimumFrameCount}..{UserCommandSequenceModel.MaximumFrameCount}.");
            var label = _commandTrainingStore.Classes.Single(item => item.Id == completedClass).Label;
            var wavePath = SaveCommandTrainingWav(completedClass!.Value, label, chunks!, _commandSpeechStartSample, _commandSpeechEndSample);
            _commandTrainingStore.AddExample(completedClass.Value, frames, wavePath);
            if (File.Exists(GetUserCommandModelPath())) File.Delete(GetUserCommandModelPath());
            Dispatcher.BeginInvoke(() =>
            {
                UpdateCommandTrainingText("Образец сохранён вместе с WAV.");
                SetStatus($"Образец «{label}» добавлен. WAV: {wavePath}");
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Dispatcher.BeginInvoke(() => SetStatus($"Не удалось сохранить образец команды: {error.Message}"));
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _coordinator?.Pause();
        SetStatus("Вывод курсора приостановлен. Остановите диагностику, затем при необходимости начните новый сеанс управления курсором.");
    }

    private async void CollectMotionExample_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out var classIndex) ||
            classIndex < 0 || classIndex >= UserMotionClassifier.RequiredClassCount) return;
        if (_cursorControlEnabled)
        {
            SetStatus("Для обучения сначала остановите управление курсором.");
            return;
        }
        if (_source is null) await StartAsync(enableCursor: false);
        if (_source is null) return;
        lock (_motionTrainingLock)
        {
            _collectingMotionClass = classIndex;
            _collectingMotionFrames = [];
            _collectingMotionAudioChunks = [];
            _motionCollectionStarted = DateTime.UtcNow;
        }
        SetStatus($"Говорите выбранный звук для направления «{DirectionName(classIndex)}»; сбор завершится автоматически.");
    }

    private async void TrainMotion_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            UserMotionClassifier trained;
            lock (_motionTrainingLock)
            {
                var examples = _motionExamples
                    .Select(items => (IReadOnlyList<float[][]>)items)
                    .ToArray();
                trained = UserMotionClassifier.Train(examples);
                trained.Save(GetUserMotionModelPath());
                _collectingMotionClass = null;
                _collectingMotionFrames = null;
                _collectingMotionAudioChunks = null;
            }
            await StartAsync(enableCursor: false);
            SetStatus($"Пользовательская модель обучена ({trained.ClassCount} направления) и активна. Курсор остаётся выключенным.");
            UpdateMotionTrainingText("Используется пользовательская модель.");
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException)
        {
            SetStatus($"Не удалось обучить модель: {error.Message}");
        }
    }

    private void CollectMotionTrainingFrame(RecognitionWithEnergyResult result)
    {
        var frames = result.ContextFeatures;
        if (frames is null || frames.Length != UserMotionClassifier.RequiredInputCount) return;
        var completedClass = -1;
        float[]? recordedAudio = null;
        lock (_motionTrainingLock)
        {
            if (_collectingMotionClass is not { } classIndex || _collectingMotionFrames is null) return;
            if (DateTime.UtcNow - _motionCollectionStarted > TimeSpan.FromSeconds(12))
            {
                _collectingMotionClass = null;
                _collectingMotionFrames = null;
                _collectingMotionAudioChunks = null;
                Dispatcher.BeginInvoke(() => SetStatus("Запись образца отменена: не удалось набрать достаточно речевых кадров за 12 секунд."));
                return;
            }
            if (_isSpeechActive) _collectingMotionFrames.Add(frames.ToArray());
            if (_collectingMotionFrames.Count < 60) return;
            completedClass = classIndex;
            _motionExamples[classIndex].Add(_collectingMotionFrames.ToArray());
            if (_collectingMotionAudioChunks is { Count: > 0 } chunks)
                recordedAudio = chunks.SelectMany(chunk => chunk).ToArray();
            _collectingMotionClass = null;
            _collectingMotionFrames = null;
            _collectingMotionAudioChunks = null;
        }
        var wavPath = recordedAudio is null ? null : SaveMotionTrainingWav(completedClass, recordedAudio);
        Dispatcher.BeginInvoke(() =>
        {
            UpdateMotionTrainingText("Образец записан.");
            SetStatus(wavPath is null
                ? $"Образец направления «{DirectionName(completedClass)}» добавлен, но WAV сохранить не удалось."
                : $"Образец направления «{DirectionName(completedClass)}» записан. WAV: {wavPath}");
        });
    }

    private void AudioChunkProduced(object? sender, AudioChunk chunk)
    {
        lock (_motionTrainingLock)
        {
            if (_collectingMotionAudioChunks is not null)
                _collectingMotionAudioChunks.Add(chunk.Samples.ToArray());
        }
        lock (_commandTrainingLock)
        {
            if (_collectingCommandClass is not null) _commandAudioChunks.Add(new AudioChunk(chunk.StartSample, chunk.Samples.ToArray()));
        }
    }

    private async void CollectCommandExample_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out var classId)) return;
        if (_cursorControlEnabled) { SetStatus("Для обучения команд сначала остановите управление курсором и кликами."); return; }
        if (_source is null) await StartAsync(enableCursor: false);
        if (_source is null) return;
        lock (_commandTrainingLock)
        {
            _collectingCommandClass = classId;
            _commandTrainingFrames = null;
            _commandSpeechStartSample = 0;
            _commandSpeechEndSample = 0;
            _commandCollectionStarted = DateTime.UtcNow;
            _commandAudioChunks.Clear();
        }
        var label = _commandTrainingStore.Classes.Single(item => item.Id == classId).Label;
        SetStatus($"Скажите «{label}» один раз; запись завершится автоматически после паузы.");
    }

    private void TrainCommand_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var model = _commandTrainingStore.Train();
            model.Save(GetUserCommandModelPath());
            TrainCommandButton.IsEnabled = false;
            UpdateCommandTrainingText("Модель обучена и сохранена. Перезапустите диагностику, чтобы она активировалась.");
            SetStatus("Пользовательская модель команд обучена. Для включения остановите и снова запустите диагностику.");
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException)
        {
            SetStatus($"Не удалось обучить команды: {error.Message}");
        }
    }

    private string SaveCommandTrainingWav(int classId, string label, IReadOnlyList<AudioChunk> chunks, long speechStart, long speechEnd)
    {
        var from = Math.Max(0, speechStart - LegacyFeatureStream.SampleRate / 10);
        var to = Math.Max(speechStart + 1, speechEnd);
        var samples = chunks.Where(chunk => chunk.StartSample < to && chunk.StartSample + chunk.Samples.Length > from)
            .SelectMany(chunk =>
            {
                var first = (int)Math.Max(0, from - chunk.StartSample);
                var last = (int)Math.Min(chunk.Samples.Length, to - chunk.StartSample);
                return chunk.Samples.AsSpan(first, Math.Max(0, last - first)).ToArray();
            }).ToArray();
        if (samples.Length == 0) throw new InvalidOperationException("Не удалось извлечь звук образца из аудиопотока.");
        var safeLabel = string.Concat(label.Where(char.IsLetterOrDigit));
        var directory = Path.Combine(GetCommandTrainingDirectory(), $"class-{classId}-{safeLabel}");
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd'T'HHmmssfff'Z'}-{Guid.NewGuid():N}.wav");
        Pcm16WaveFile.WriteMono(path, LegacyFeatureStream.SampleRate, samples);
        return path;
    }

    private void UpdateCommandTrainingText(string suffix)
    {
        var counts = _commandTrainingStore.Counts;
        var items = _commandTrainingStore.Classes.Select(item => $"{item.Label} {counts[item.Id]}/{CommandTrainingStore.MinimumExamplesPerClass}");
        CommandTrainingText.Text = $"Образцы команд: {string.Join(", ", items)}. {suffix}";
        TrainCommandButton.IsEnabled = counts.Values.All(count => count >= CommandTrainingStore.MinimumExamplesPerClass);
    }

    private static string GetCommandTrainingManifestPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocalJoystick", "commands", "training.json");
    private static string GetCommandTrainingDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocalJoystick", "training", "commands");
    private static string GetUserCommandModelPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocalJoystick", "commands", "user-model.json");

    private static string? SaveMotionTrainingWav(int classIndex, float[] samples)
    {
        try
        {
            var directory = Path.Combine(GetUserTrainingDirectory(), $"class-{classIndex}-{DirectionName(classIndex)}");
            var filename = $"{DateTime.UtcNow:yyyyMMdd'T'HHmmssfff'Z'}-{Guid.NewGuid():N}.wav";
            var path = Path.Combine(directory, filename);
            Pcm16WaveFile.WriteMono(path, LegacyFeatureStream.SampleRate, samples);
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void UpdateMotionTrainingText(string suffix)
    {
        lock (_motionTrainingLock)
        {
            var counts = _motionExamples.Select((examples, index) => $"{DirectionName(index)} {_motionExamples[index].Count}/{UserMotionClassifier.MinimumExamplesPerClass}");
            MotionTrainingText.Text = $"Образцы: {string.Join(", ", counts)}. {suffix}";
            TrainMotionButton.IsEnabled = _motionExamples.All(examples => examples.Count >= UserMotionClassifier.MinimumExamplesPerClass);
        }
    }

    private static string DirectionName(int index) => index switch
    {
        0 => "вверх", 1 => "вправо", 2 => "вниз", 3 => "влево",
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private static string GetUserMotionModelPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocalJoystick", "motion.user.json");

    private static string GetUserTrainingDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocalJoystick", "training", "motion");

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopAsync();

    private async Task StopAsync()
    {
        _coordinator?.EmergencyStop();
        _cursorControlEnabled = false;
        var source = _source;
        if (source is not null) await source.StopAsync();
        _cancellation?.Cancel();
        var task = _captureTask;
        if (task is not null) await task;
        _captureTask = null;
        _cancellation?.Dispose();
        _cancellation = null;
        _source = null;
        _coordinator = null;
        lock (_motionTrainingLock)
        {
            _collectingMotionClass = null;
            _collectingMotionFrames = null;
            _collectingMotionAudioChunks = null;
        }
        AudioDiagnosticsText.Text = "Аудиопоток остановлен.";
        ClickDiagnosticsText.Text = "Распознавание команд остановлено.";
        SetRunningState(false);
    }

    protected override async void OnClosed(EventArgs e)
    {
        await StopAsync();
        SaveSettings();
        base.OnClosed(e);
    }

    private void SetRunningState(bool running)
    {
        StartDiagnosticsButton.IsEnabled = !running;
        EnableCursorButton.IsEnabled = !running;
        SetupButton.IsEnabled = !running;
        PauseButton.IsEnabled = running;
        StopButton.IsEnabled = running;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void ApplySettings(DesktopSettings? settings)
    {
        if (settings is null) return;
        _preferredMicrophoneIndex = settings.MicrophoneIndex;
        if (File.Exists(settings.NormsPath)) NormsPathBox.Text = settings.NormsPath;
        if (File.Exists(settings.ModelPath)) ModelPathBox.Text = settings.ModelPath;
        if (File.Exists(settings.ParametersPath)) ParametersPathBox.Text = settings.ParametersPath;
        if (File.Exists(settings.PowerPath)) PowerPathBox.Text = settings.PowerPath;
        if (File.Exists(settings.RegressionPath)) RegressionPathBox.Text = settings.RegressionPath;
        if (!string.IsNullOrWhiteSpace(settings.CommandModelPath) && File.Exists(settings.CommandModelPath))
            CommandModelPathBox.Text = settings.CommandModelPath;
        if (!string.IsNullOrWhiteSpace(settings.CommandDictionaryPath) && File.Exists(settings.CommandDictionaryPath))
            CommandDictionaryPathBox.Text = settings.CommandDictionaryPath;
        if (float.IsFinite(settings.PixelsPerVelocityUnit) && settings.PixelsPerVelocityUnit > 0f)
            PixelScaleBox.Text = settings.PixelsPerVelocityUnit.ToString(CultureInfo.InvariantCulture);
        if (float.IsFinite(settings.CommandDistanceThresholdMultiplier) &&
            settings.CommandDistanceThresholdMultiplier >= UserCommandSequenceModel.MinimumThresholdMultiplier &&
            settings.CommandDistanceThresholdMultiplier <= UserCommandSequenceModel.MaximumThresholdMultiplier)
            CommandThresholdMultiplierBox.Text = settings.CommandDistanceThresholdMultiplier.ToString(CultureInfo.InvariantCulture);
    }

    private float ReadPixelScaleOrDefault() =>
        float.TryParse(PixelScaleBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) &&
        float.IsFinite(scale) && scale > 0f ? scale : .1f;

    private float ReadCommandThresholdMultiplierOrDefault() =>
        float.TryParse(CommandThresholdMultiplierBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var multiplier) &&
        float.IsFinite(multiplier) && multiplier >= UserCommandSequenceModel.MinimumThresholdMultiplier &&
        multiplier <= UserCommandSequenceModel.MaximumThresholdMultiplier ? multiplier : 1f;

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(new DesktopSettings(
                (MicrophoneBox.SelectedItem as MicrophoneOption)?.Index ?? _preferredMicrophoneIndex,
                NormsPathBox.Text, ModelPathBox.Text, ParametersPathBox.Text, PowerPathBox.Text, RegressionPathBox.Text,
                ReadPixelScaleOrDefault(), CommandModelPathBox.Text, CommandDictionaryPathBox.Text,
                ReadCommandThresholdMultiplierOrDefault()));
        }
        catch (IOException)
        {
            // Settings persistence must not block an accessibility control session.
        }
    }

    private static IClassifier LoadClassifier(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".wts" => LegacyMlpModel.Load(path),
        ".rsc" => LegacyRscModel.Load(path),
        ".mlrsc" => LegacyMlrscModel.Load(path),
        _ => throw new ArgumentException("Модель должна иметь расширение .wts, .rsc или .mlrsc.", nameof(path))
    };

}
