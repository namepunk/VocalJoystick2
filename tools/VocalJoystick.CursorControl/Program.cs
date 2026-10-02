using System.Globalization;
using VocalJoystick.Audio;
using VocalJoystick.Core;
using VocalJoystick.Features;
using VocalJoystick.Input;
using VocalJoystick.Models;
using VocalJoystick.Motion;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Cursor control can run only on Windows.");
    return 2;
}

if (args is ["--list-devices"])
{
    foreach (var (name, index) in WindowsMicrophoneSource.GetDeviceNames().Select((name, index) => (name, index)))
        Console.WriteLine($"{index}: {name}");
    return 0;
}

if (args.Length != 9 || !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var deviceNumber) ||
    args[6] != "--pixels-per-velocity-unit" ||
    !float.TryParse(args[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var pixelsPerVelocityUnit) ||
    !float.IsFinite(pixelsPerVelocityUnit) || pixelsPerVelocityUnit <= 0f || args[8] != "--enable-cursor")
{
    Console.Error.WriteLine("Usage: VocalJoystick.CursorControl --list-devices");
    Console.Error.WriteLine("   or: VocalJoystick.CursorControl <norms> <model> <device> <prm.cfg> <user.power> <vj.lr> --pixels-per-velocity-unit <positive-number> --enable-cursor");
    Console.Error.WriteLine("The --enable-cursor flag is required. This tool never sends clicks.");
    return 2;
}

try
{
    var normalizer = FeatureNormalizer.Load(args[0]);
    var classifier = LoadClassifier(args[1]);
    var motion = LegacyMotionControllerFactory.Create(args[3], args[4], args[5], classifier.ClassCount);
    var gate = new CursorSafetyGate();
    var coordinator = new VoiceCursorCoordinator(
        motion,
        new MotionDeltaQuantizer(pixelsPerVelocityUnit),
        gate,
        new WindowsCursorOutput());
    await using var source = new WindowsMicrophoneSource(deviceNumber);
    var runner = new AudioRecognitionRunner(source, new LegacyRecognitionPipeline(normalizer, classifier));
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        coordinator.EmergencyStop();
        cancellation.Cancel();
    };
    var resultCount = 0;
    runner.RecognitionWithEnergyProduced += (_, result) =>
    {
        var cursor = coordinator.Process(result);
        resultCount++;
        if (resultCount % 10 == 0)
            Console.WriteLine($"sample={cursor.StartSample}; delta=({cursor.Delta.X},{cursor.Delta.Y}); applied={cursor.AppliedToCursor}; velocity={cursor.Motion.Velocity:F4}");
    };

    gate.Enable();
    Console.WriteLine("Cursor control is active. Press Ctrl+C for an emergency stop. No click action is implemented.");
    try
    {
        await runner.RunAsync(cancellation.Token);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        await source.StopAsync();
    }
    finally
    {
        coordinator.EmergencyStop();
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Cannot start or continue cursor control: {error.Message}");
    return 1;
}

static IClassifier LoadClassifier(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".wts" => LegacyMlpModel.Load(path),
    ".rsc" => LegacyRscModel.Load(path),
    ".mlrsc" => LegacyMlrscModel.Load(path),
    _ => throw new ArgumentException("Model must have .wts, .rsc, or .mlrsc extension.", nameof(path))
};
