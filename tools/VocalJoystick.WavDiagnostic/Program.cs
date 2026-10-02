using System.Buffers.Binary;
using System.Text;
using VocalJoystick.Core;
using VocalJoystick.Features;
using VocalJoystick.Models;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: VocalJoystick.WavDiagnostic <mono-16k-pcm.wav> <vj.norms> <model.wts|model.rsc|model.mlrsc>");
    return 2;
}

try
{
    var samples = ReadLegacyPcmWav(args[0]);
    var normalizer = FeatureNormalizer.Load(args[1]);
    var classifier = LoadClassifier(args[2]);
    var stream = new LegacyFeatureStream();
    var recognition = new LegacyRecognitionPipeline(normalizer, classifier);
    var featureFrames = 0;
    var classifiedFrames = 0;
    float[]? latestScores = null;

    foreach (var featureFrame in stream.Push(samples))
    {
        featureFrames++;
        if (recognition.TryClassify(featureFrame.Features, out var scores))
        {
            classifiedFrames++;
            latestScores = scores;
        }
    }

    Console.WriteLine($"samples: {samples.Length}");
    Console.WriteLine($"feature-frames: {featureFrames}");
    Console.WriteLine($"classified-frames: {classifiedFrames}");
    if (latestScores is not null)
    {
        var best = Array.IndexOf(latestScores, latestScores.Max());
        Console.WriteLine($"last-best-class: {best}");
        Console.WriteLine("last-scores: " + string.Join(", ", latestScores.Select(score => score.ToString("G9", System.Globalization.CultureInfo.InvariantCulture))));
    }
    return 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or FormatException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static IClassifier LoadClassifier(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".wts" => LegacyMlpModel.Load(path),
    ".rsc" => LegacyRscModel.Load(path),
    ".mlrsc" => LegacyMlrscModel.Load(path),
    _ => throw new ArgumentException("Model must have .wts, .rsc, or .mlrsc extension.", nameof(path))
};

static float[] ReadLegacyPcmWav(string path)
{
    var bytes = File.ReadAllBytes(path);
    if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
        Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
        throw new InvalidDataException("Expected a RIFF/WAVE file.");

    short format = 0;
    short channels = 0;
    var sampleRate = 0;
    short bitsPerSample = 0;
    ReadOnlySpan<byte> data = default;
    var offset = 12;
    while (offset + 8 <= bytes.Length)
    {
        var chunkName = Encoding.ASCII.GetString(bytes, offset, 4);
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
        offset += 8;
        if (length < 0 || length > bytes.Length - offset) throw new InvalidDataException("Invalid WAV chunk length.");
        var chunk = bytes.AsSpan(offset, length);
        if (chunkName == "fmt ")
        {
            if (chunk.Length < 16) throw new InvalidDataException("WAV fmt chunk is too short.");
            format = BinaryPrimitives.ReadInt16LittleEndian(chunk);
            channels = BinaryPrimitives.ReadInt16LittleEndian(chunk[2..]);
            sampleRate = BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]);
            bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(chunk[14..]);
        }
        else if (chunkName == "data") data = chunk;
        offset += length + (length & 1);
    }
    if (format != 1 || channels != 1 || sampleRate != LegacyFeatureStream.SampleRate || bitsPerSample != 16 || data.IsEmpty || data.Length % 2 != 0)
        throw new InvalidDataException("WAV must be mono 16-bit PCM at 16 kHz.");

    var samples = new float[data.Length / 2];
    for (var index = 0; index < samples.Length; index++)
        samples[index] = BinaryPrimitives.ReadInt16LittleEndian(data[(index * 2)..]);
    return samples;
}
