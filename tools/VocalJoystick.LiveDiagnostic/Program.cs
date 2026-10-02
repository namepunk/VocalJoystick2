using VocalJoystick.Audio;
using VocalJoystick.Core;
using VocalJoystick.Features;
using VocalJoystick.Models;
using VocalJoystick.Motion;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Live microphone diagnostics can run only on Windows.");
    return 2;
}

if (args is ["--list-devices"])
{
    foreach (var (name, index) in WindowsMicrophoneSource.GetDeviceNames().Select((name, index) => (name, index)))
        Console.WriteLine($"{index}: {name}");
    return 0;
}

if (args.Length is not (3 or 7) || !int.TryParse(args[2], out var deviceNumber) ||
    (args.Length == 7 && args[3] != "--motion"))
{
    Console.Error.WriteLine("Usage: VocalJoystick.LiveDiagnostic --list-devices");
    Console.Error.WriteLine("   or: VocalJoystick.LiveDiagnostic <vj.norms> <model.wts|model.rsc|model.mlrsc> <device-number>");
    Console.Error.WriteLine("   or: VocalJoystick.LiveDiagnostic <norms> <model> <device-number> --motion <prm.cfg> <user.power> <vj.lr>");
    return 2;
}

try
{
    var normalizer = FeatureNormalizer.Load(args[0]);
    var classifier = LoadClassifier(args[1]);
    var motion = args.Length == 7
        ? LegacyMotionControllerFactory.Create(args[4], args[5], args[6], classifier.ClassCount)
        : null;
    await using var source = new WindowsMicrophoneSource(deviceNumber);
    var runner = new AudioRecognitionRunner(source, new LegacyRecognitionPipeline(normalizer, classifier));
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
    var resultCount = 0;
    runner.RecognitionWithEnergyProduced += (_, result) =>
    {
        resultCount++;
        if (resultCount % 10 != 0) return;
        var best = Array.IndexOf(result.Scores, result.Scores.Max());
        var message = $"sample={result.StartSample}; rms={result.RootMeanSquareEnergy:F1}; zc={result.ZeroCrossings}; best-class={best}; scores=" +
            string.Join(",", result.Scores.Select(score => score.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)));
        if (motion is not null)
        {
            var sample = motion.Update(result.RootMeanSquareEnergy, result.Scores);
            message += $"; motion-x={sample.XDirection:F4}; motion-y={sample.YDirection:F4}; velocity={sample.Velocity:F4}; global-velocity={sample.GlobalVelocity:F4}";
        }
        Console.WriteLine(message);
    };
    Console.WriteLine(motion is null
        ? "Capturing. No cursor or click action is performed. Press Ctrl+C to stop."
        : "Capturing with legacy motion diagnostics. No cursor or click action is performed. Press Ctrl+C to stop.");
    try
    {
        await runner.RunAsync(cancellation.Token);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        await source.StopAsync();
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Cannot start or continue microphone capture: {error.Message}");
    return 1;
}

static IClassifier LoadClassifier(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".wts" => LegacyMlpModel.Load(path),
    ".rsc" => LegacyRscModel.Load(path),
    ".mlrsc" => LegacyMlrscModel.Load(path),
    _ => throw new ArgumentException("Model must have .wts, .rsc, or .mlrsc extension.", nameof(path))
};
