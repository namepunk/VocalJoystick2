using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using VocalJoystick.Audio;
using VocalJoystick.Commands;
using VocalJoystick.Core;
using VocalJoystick.Input;
using VocalJoystick.Models;
using VocalJoystick.Motion;

namespace VocalJoystick.Features.Tests;

public sealed class FeatureNormalizerTests
{
    [Fact]
    public void Normalize_AppliesMeanAndInverseStandardDeviation()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "2\n1\n-2\n2\n0.5\n4\n");
            var normalizer = FeatureNormalizer.Load(path);
            var actual = new float[2];
            normalizer.Normalize([5f, -1f], actual);
            Assert.Equal([2f, 4f], actual);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class Pcm16WaveFileTests
{
    [Fact]
    public void WriteMono_ProducesPcm16WaveAtRequestedSampleRate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vj-wave-{Guid.NewGuid():N}.wav");
        try
        {
            Pcm16WaveFile.WriteMono(path, 16_000, [-32768f, -12.6f, 0f, 123.4f, 32767f]);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
            Assert.Equal(16_000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24, 4)));
            Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22, 2)));
            Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(34, 2)));
            Assert.Equal(new short[] { -32768, -13, 0, 123, 32767 }, Enumerable.Range(0, 5)
                .Select(index => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + index * 2, 2))).ToArray());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

public sealed class UserMotionClassifierTests
{
    [Fact]
    public void Train_LearnsFourDirectionPrototypesAndRoundTrips()
    {
        var training = Enumerable.Range(0, UserMotionClassifier.RequiredClassCount)
            .Select(classIndex => (IReadOnlyList<float[][]>)Enumerable.Range(0, 3)
                .Select(exampleIndex => Enumerable.Range(0, 4)
                    .Select(_ => Enumerable.Range(0, UserMotionClassifier.RequiredInputCount)
                        .Select(feature => feature == classIndex ? 4f + exampleIndex * .01f : 0f).ToArray())
                    .ToArray())
                .ToArray())
            .ToArray();
        var model = UserMotionClassifier.Train(training);

        for (var classIndex = 0; classIndex < UserMotionClassifier.RequiredClassCount; classIndex++)
        {
            var probe = new float[UserMotionClassifier.RequiredInputCount];
            probe[classIndex] = 4f;
            var scores = model.Classify(probe);
            Assert.Equal(classIndex, Array.IndexOf(scores, scores.Max()));
            Assert.InRange(scores[classIndex], .7f, 1f);
        }

        var path = Path.Combine(Path.GetTempPath(), $"vj-motion-{Guid.NewGuid():N}.json");
        try
        {
            model.Save(path);
            var loaded = UserMotionClassifier.Load(path);
            Assert.Equal(model.Classify(new float[UserMotionClassifier.RequiredInputCount]),
                loaded.Classify(new float[UserMotionClassifier.RequiredInputCount]));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Train_RequiresMinimumExamplesForEveryClass()
    {
        var training = Enumerable.Range(0, UserMotionClassifier.RequiredClassCount)
            .Select(_ => (IReadOnlyList<float[][]>)new[] { new[] { new float[UserMotionClassifier.RequiredInputCount] } })
            .ToArray();
        Assert.Throws<InvalidOperationException>(() => UserMotionClassifier.Train(training));
    }
}

public sealed class UserCommandSequenceModelTests
{
    [Fact]
    public void Classify_ThresholdMultiplierControlsAcceptanceStrictness()
    {
        var classes = new[]
        {
            new CommandClassDefinition(0, "К", "left_click"),
            new CommandClassDefinition(1, "Ч", "right_click")
        };
        var examples = classes.SelectMany(definition => Enumerable.Range(0, 3).Select(repetition =>
            new CommandTrainingExample(definition.Id, Sequence(definition.Id * 10f + repetition * .01f)))).ToArray();
        var model = UserCommandSequenceModel.Train(classes, examples);

        var strict = model.Classify(Sequence(.8f), thresholdMultiplier: .1f);
        var permissive = model.Classify(Sequence(.8f), thresholdMultiplier: 2f);

        Assert.False(strict.IsAccepted);
        Assert.Equal(CommandRejectionReason.DistanceTooLarge, strict.Rejection);
        Assert.True(permissive.IsAccepted);
        Assert.True(permissive.AcceptanceThreshold > strict.AcceptanceThreshold);
    }

    [Fact]
    public void Classify_AlignsUtterancesWithDifferentDurationsAndRecognizerRecoversAfterRejection()
    {
        var classes = new[]
        {
            new CommandClassDefinition(0, "К", "left_click"),
            new CommandClassDefinition(1, "Ч", "right_click")
        };
        var examples = new List<CommandTrainingExample>();
        foreach (var definition in classes)
        {
            examples.Add(new CommandTrainingExample(definition.Id, Sequence(definition.Id * 10f, 4)));
            examples.Add(new CommandTrainingExample(definition.Id, Sequence(definition.Id * 10f, 12)));
            examples.Add(new CommandTrainingExample(definition.Id, Sequence(definition.Id * 10f, 30)));
        }
        var model = UserCommandSequenceModel.Train(classes, examples);

        var durationVariant = model.Classify(Sequence(0f, 28));
        Assert.True(durationVariant.IsAccepted);
        Assert.Equal(0, durationVariant.ClassId);
        Assert.True(float.IsFinite(durationVariant.BestDistance));

        var recognizer = new UserCommandSequenceRecognizer(model);
        var unfamiliar = Sequence(100f, 7);
        for (var index = 0; index < unfamiliar.Length; index++)
            recognizer.Push(unfamiliar[index], startedSpeech: index == 0, isSpeech: true, startedSilence: false);
        Assert.False(recognizer.EndOfSpeech().IsAccepted);
        var valid = Sequence(10f, 9);
        for (var index = 0; index < valid.Length; index++)
            recognizer.Push(valid[index], startedSpeech: index == 0, isSpeech: true, startedSilence: false);
        var nextDecision = recognizer.EndOfSpeech();
        Assert.True(nextDecision.IsAccepted);
        Assert.Equal(1, nextDecision.ClassId);
    }

    [Fact]
    public void Train_SupportsFiveClassesAndRejectsUnfamiliarSequences()
    {
        var classes = Enumerable.Range(0, 5)
            .Select(index => new CommandClassDefinition(index, $"class-{index}", $"action-{index}")).ToArray();
        var examples = classes.SelectMany(definition => Enumerable.Range(0, 3).Select(repetition =>
            new CommandTrainingExample(definition.Id, Sequence(definition.Id * 10f + repetition * .01f)))).ToArray();
        var model = UserCommandSequenceModel.Train(classes, examples);

        for (var index = 0; index < classes.Length; index++)
        {
            var decision = model.Classify(Sequence(index * 10f + .005f));
            Assert.True(decision.IsAccepted);
            Assert.Equal(index, decision.ClassId);
            Assert.Equal($"action-{index}", decision.ActionId);
        }

        var unfamiliar = model.Classify(Sequence(1000f));
        Assert.False(unfamiliar.IsAccepted);
        Assert.Equal(CommandRejectionReason.DistanceTooLarge, unfamiliar.Rejection);
    }

    [Fact]
    public void ModelAndTrainingStore_RoundTripPersistedTemplatesAndWaveReference()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vj-command-{Guid.NewGuid():N}");
        var manifest = Path.Combine(directory, "training.json");
        var wav = Path.Combine(directory, "sample.wav");
        var modelPath = Path.Combine(directory, "model.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(wav, [1, 2, 3]);
            var store = new CommandTrainingStore(manifest);
            store.AddExample(0, Sequence(1f), wav);
            var restoredStore = new CommandTrainingStore(manifest);
            Assert.Equal(1, restoredStore.Counts[0]);
            Assert.Equal(Path.GetFullPath(wav), restoredStore.ResolveWavePath(restoredStore.Examples[0]));

            var classes = restoredStore.Classes;
            var examples = new List<CommandTrainingExample>();
            foreach (var definition in classes)
                for (var repeat = 0; repeat < 3; repeat++)
                    examples.Add(new CommandTrainingExample(definition.Id, Sequence(definition.Id * 10f + repeat * .01f)));
            var model = UserCommandSequenceModel.Train(classes, examples);
            model.Save(modelPath);
            var loaded = UserCommandSequenceModel.Load(modelPath);
            Assert.Equal(model.Classify(Sequence(1f)), loaded.Classify(Sequence(1f)));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static float[][] Sequence(float value, int frameCount = 6) => Enumerable.Range(0, frameCount)
        .Select(frame =>
        {
            var values = new float[UserCommandSequenceModel.FeatureCount];
            values[0] = value;
            values[1] = frame * .1f;
            return values;
        }).ToArray();
}

public sealed class FrameContextBuilderTests
{
    [Fact]
    public void TryBuild_ReturnsFalseUntilContextIsComplete()
    {
        var builder = new FrameContextBuilder(2, 3, FrameContextOrder.OldestToNewest);
        var context = new float[6];
        builder.AddFrame([1f, 2f]);
        Assert.False(builder.TryBuild(context));
    }

    [Fact]
    public void TryBuild_KeepsMostRecentFramesInSelectedOrder()
    {
        var builder = new FrameContextBuilder(2, 3, FrameContextOrder.OldestToNewest);
        var context = new float[6];
        builder.AddFrame([1f, 2f]);
        builder.AddFrame([3f, 4f]);
        builder.AddFrame([5f, 6f]);
        builder.AddFrame([7f, 8f]);
        Assert.True(builder.TryBuild(context));
        Assert.Equal([3f, 4f, 5f, 6f, 7f, 8f], context);
    }

    [Fact]
    public void TryBuild_CanEmitNewestFrameFirst()
    {
        var builder = new FrameContextBuilder(1, 3, FrameContextOrder.NewestToOldest);
        var context = new float[3];
        builder.AddFrame([1f]);
        builder.AddFrame([2f]);
        builder.AddFrame([3f]);
        Assert.True(builder.TryBuild(context));
        Assert.Equal([3f, 2f, 1f], context);
    }
}

public sealed class AudioFramerTests
{
    [Fact]
    public void Push_EmitsOverlappingFramesAcrossInputChunks()
    {
        var framer = new AudioFramer(new AudioFrameSpec(16_000, 4, 2));
        Assert.Empty(framer.Push([1f, 2f, 3f]));

        var frames = framer.Push([4f, 5f, 6f]);

        Assert.Collection(frames,
            frame => { Assert.Equal(0, frame.StartSample); Assert.Equal([1f, 2f, 3f, 4f], frame.Samples); },
            frame => { Assert.Equal(2, frame.StartSample); Assert.Equal([3f, 4f, 5f, 6f], frame.Samples); });
    }
}

public sealed class LinearResamplerTests
{
    [Fact]
    public void Resample_LinearlyInterpolatesAndPreservesEndpoints()
    {
        var actual = LinearResampler.Resample([0f, 10f, 20f, 30f, 40f], 4, 8);

        Assert.Equal([0f, 5f, 10f, 15f, 20f, 25f, 30f, 35f, 40f], actual);
    }
}

public sealed class FrameMeasurementsTests
{
    [Fact]
    public void MeanSquareEnergy_ComputesAveragePower() =>
        Assert.Equal(2.5f, FrameMeasurements.MeanSquareEnergy([1f, -2f]));

    [Fact]
    public void ZeroCrossingCount_IgnoresZerosAndCountsSignChanges() =>
        Assert.Equal(2, FrameMeasurements.ZeroCrossingCount([-1f, 0f, 2f, 0f, -3f]));
}

public sealed class MfccExtractorTests
{
    private static readonly MfccConfig Config = new(16_000, 8, 4, 0, 8_000);

    [Fact]
    public void Extract_ReturnsConfiguredFiniteCoefficientCount()
    {
        var samples = Enumerable.Range(0, 64).Select(index => MathF.Sin(2 * MathF.PI * index / 16)).ToArray();

        var coefficients = new MfccExtractor(Config).Extract(samples);

        Assert.Equal(4, coefficients.Length);
        Assert.All(coefficients, value => Assert.True(float.IsFinite(value)));
    }

    [Fact]
    public void Extract_IsDeterministicForSameFrame()
    {
        var samples = Enumerable.Range(0, 64).Select(index => index % 2 == 0 ? .5f : -.5f).ToArray();
        var extractor = new MfccExtractor(Config);

        Assert.Equal(extractor.Extract(samples), extractor.Extract(samples));
    }
}

public sealed class LegacyMfccExtractorTests
{
    [Fact]
    public void CreateFilterBank_MatchesRecoveredBinVerticesAndEdgeWeights()
    {
        Assert.Equal(
            [2, 5, 8, 11, 14, 18, 23, 27, 33, 38, 45, 52, 60, 69, 79, 89, 101, 115, 129, 145, 163, 183, 205, 229, 256],
            LegacyMfccExtractor.GetFilterBinPoints());

        var filters = LegacyMfccExtractor.CreateFilterBank();
        Assert.Equal(23, filters.Length);
        Assert.Equal([.25f, .5f, .75f, 1f, .75f, .5f, .25f], filters[0]);
        Assert.Equal(52, filters[^1].Length);
        Assert.Equal(.04f, filters[^1][0], precision: 6);
        Assert.Equal(1f, filters[^1][24], precision: 6);
        Assert.Equal(1f / 28f, filters[^1][^1], precision: 6);
    }

    [Fact]
    public void Extract_RejectsShortFrame() =>
        Assert.Throws<ArgumentException>(() => new LegacyMfccExtractor().Extract(new float[LegacyMfccExtractor.FrameLength - 1]));

    [Fact]
    public void Extract_SilenceUsesLegacyLogFloor()
    {
        var features = new LegacyMfccExtractor().Extract(new float[LegacyMfccExtractor.RequiredSampleCount]);

        Assert.Equal(13, features.Length);
        Assert.Equal(-50f, features[^1]);
        Assert.All(features, feature => Assert.True(float.IsFinite(feature)));
    }

    [Fact]
    public void Extract_UsesRecoveredRecursiveDcBlockerForEnergy()
    {
        var samples = new float[LegacyMfccExtractor.FrameLength];
        samples[0] = 1f;

        var features = new LegacyMfccExtractor().Extract(samples);

        var expectedEnergy = 1d + Enumerable.Range(0, LegacyMfccExtractor.FrameLength - 2)
            .Sum(index => Math.Pow(.001d * Math.Pow(.999d, index), 2));
        Assert.Equal(Math.Log(expectedEnergy), features[^1], precision: 6);
    }
}

public sealed class LegacyFeatureStreamTests
{
    [Fact]
    public void Push_EmitsCentreFrameWithRecoveredDeltasAcrossAudioChunks()
    {
        var samples = Enumerable.Range(0, 1040)
            .Select(index => 2000f * MathF.Sin(2 * MathF.PI * 440 * index / 16000)).ToArray();
        var stream = new LegacyFeatureStream();

        Assert.Empty(stream.Push(samples.AsSpan(0, 503)));
        var frames = stream.Push(samples.AsSpan(503));

        var actual = Assert.Single(frames);
        Assert.Equal(320, actual.StartSample);
        Assert.Equal(26, actual.Features.Length);

        var extractor = new LegacyMfccExtractor();
        var staticFrames = Enumerable.Range(0, 5)
            .Select(index => extractor.Extract(samples.AsSpan(index * 160, 400))).ToArray();
        Assert.Equal(staticFrames[2], actual.Features[..13]);
        var expectedDelta = new float[13];
        DeltaFeatures.ComputeCentered(staticFrames, 2, expectedDelta);
        Assert.Equal(expectedDelta, actual.Features[13..]);
    }
}

public sealed class LegacyFrameEnergyStreamTests
{
    [Fact]
    public void Push_UsesRecovered960SampleRmsAndCentreClipping()
    {
        var stream = new LegacyFrameEnergyStream();
        var samples = Enumerable.Repeat(9f, LegacyFrameEnergyStream.FrameLength).ToArray();
        samples[0] = 10f;
        samples[1] = -10f;

        var frames = stream.Push(samples);

        var frame = Assert.Single(frames);
        Assert.Equal(0, frame.StartSample);
        Assert.Equal(MathF.Sqrt(200f / LegacyFrameEnergyStream.FrameLength), frame.RootMeanSquare, precision: 6);
        Assert.Equal(1, frame.ZeroCrossings);
    }

    [Fact]
    public void Push_EmitsOverlappingPowerFramesAcrossChunks()
    {
        var stream = new LegacyFrameEnergyStream();
        var samples = Enumerable.Repeat(20f, LegacyFrameEnergyStream.FrameLength + LegacyFrameEnergyStream.HopLength).ToArray();

        Assert.Empty(stream.Push(samples.AsSpan(0, 700)));
        var frames = stream.Push(samples.AsSpan(700));

        Assert.Collection(frames,
            frame => { Assert.Equal(0, frame.StartSample); Assert.Equal(20f, frame.RootMeanSquare); },
            frame => { Assert.Equal(160, frame.StartSample); Assert.Equal(20f, frame.RootMeanSquare); });
    }
}

public sealed class LegacyPitchCorrelationTests
{
    [Fact]
    public void Compute_UsesLegacyFrameAndLagRangeForAStableSine()
    {
        var samples = Enumerable.Range(0, LegacyPitchCorrelation.LegacyPitchFrameLength)
            .Select(index => MathF.Sin(2f * MathF.PI * 440f * index / 16_000f)).ToArray();

        var correlation = LegacyPitchCorrelation.Compute(samples);
        var bestLag = Enumerable.Range(LegacyPitchCorrelation.MinimumLag, 29)
            .OrderByDescending(lag => correlation[lag])
            .First();

        Assert.Equal(1f, correlation[0]);
        Assert.InRange(bestLag, 35, 38);
        Assert.InRange(correlation[bestLag], .99f, 1f);
    }

    [Fact]
    public void Compute_UsesLegacyNearZeroFallback()
    {
        var correlation = LegacyPitchCorrelation.Compute(new float[LegacyPitchCorrelation.LegacyPitchFrameLength]);

        Assert.All(correlation, value => Assert.Equal(1f, value));
    }
}

public sealed class LegacyMlpModelTests
{
    [Fact]
    public void Classify_UsesLegacyRowMajorWeightsDecreasingSigmoidAndSoftmax()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                2
                2
                2
                4 # weigvec
                1
                2
                3
                4
                4 # weigvec
                1
                -1
                -2
                0.5
                2 # biasvec
                0.25
                -0.5
                2 # biasvec
                0.1
                -0.2
                """);
            var model = LegacyMlpModel.Load(path);

            var actual = model.Classify([.5f, -1f]);

            var hidden0 = 1f / (1f + MathF.Exp(1f * .5f + 2f * -1f + .25f));
            var hidden1 = 1f / (1f + MathF.Exp(3f * .5f + 4f * -1f - .5f));
            var score0 = MathF.Exp(hidden0 - hidden1 + .1f);
            var score1 = MathF.Exp(-2f * hidden0 + .5f * hidden1 - .2f);
            var total = score0 + score1;
            Assert.Equal(score0 / total, actual[0], precision: 6);
            Assert.Equal(score1 / total, actual[1], precision: 6);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Classify_RejectsWrongInputCount()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "1\n1\n1\n1\n0\n1\n0\n1\n0\n1\n0\n");
            var model = LegacyMlpModel.Load(path);
            Assert.Throws<ArgumentException>(() => model.Classify([]));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class LegacyMlrscModelTests
{
    [Fact]
    public void Classify_UsesRecoveredBlocksDecreasingSigmoidAndSquaredScores()
    {
        var path = Path.GetTempFileName();
        try
        {
            var values = new float[]
            {
                // Two class matrices, each 2x2 and stored row-major.
                1f, 0f, 0f, 1f, 2f, 0f, 0f, .5f,
                // Per-class shifts.
                .2f, .3f, .1f, .4f,
                // Two input-to-hidden rows, then hidden biases.
                1f, -1f, .5f, 2f, .1f, -.2f
            };
            var bytes = new byte[4 * sizeof(int) + values.Length * sizeof(float)];
            var offset = 0;
            foreach (var dimension in new[] { 26, 2, 2, 2 })
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)), dimension);
                offset += sizeof(int);
            }
            foreach (var value in values)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, sizeof(float)), BitConverter.SingleToInt32Bits(value));
                offset += sizeof(float);
            }
            File.WriteAllBytes(path, bytes);

            var actual = LegacyMlrscModel.Load(path).Classify([.3f, -.4f]);

            var hidden0 = 1f / (1f + MathF.Exp(.3f + .4f + .1f));
            var hidden1 = 1f / (1f + MathF.Exp(.15f - .8f - .2f));
            var score0 = MathF.Pow(hidden0 - .2f, 2) + MathF.Pow(hidden1 - .3f, 2);
            var score1 = MathF.Pow(2f * (hidden0 - .1f), 2) + MathF.Pow(.5f * (hidden1 - .4f), 2);
            Assert.Equal(score0 / (score0 + score1), actual[0], precision: 6);
            Assert.Equal(score1 / (score0 + score1), actual[1], precision: 6);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class LegacyRscModelTests
{
    [Fact]
    public void Classify_UsesRecoveredShiftMatrixAndSquaredScores()
    {
        var path = Path.GetTempFileName();
        try
        {
            var values = new float[]
            {
                // Two row-major 2x2 class matrices, then class shifts.
                1f, 0f, 0f, 1f, 2f, 0f, 0f, .5f,
                .2f, .3f, .1f, .4f
            };
            var bytes = new byte[3 * sizeof(int) + values.Length * sizeof(float)];
            var offset = 0;
            foreach (var dimension in new[] { 26, 2, 2 })
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)), dimension);
                offset += sizeof(int);
            }
            foreach (var value in values)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, sizeof(float)), BitConverter.SingleToInt32Bits(value));
                offset += sizeof(float);
            }
            File.WriteAllBytes(path, bytes);

            var actual = LegacyRscModel.Load(path).Classify([.3f, -.4f]);

            var score0 = MathF.Pow(.3f - .2f, 2) + MathF.Pow(-.4f - .3f, 2);
            var score1 = MathF.Pow(2f * (.3f - .1f), 2) + MathF.Pow(.5f * (-.4f - .4f), 2);
            Assert.Equal(score0 / (score0 + score1), actual[0], precision: 6);
            Assert.Equal(score1 / (score0 + score1), actual[1], precision: 6);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class LegacyRecognitionPipelineTests
{
    [Fact]
    public void TryClassify_NormalizesAndBuildsSevenFramesOldestFirst()
    {
        var normalizerPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(normalizerPath,
                "26\n" + string.Join('\n', Enumerable.Repeat("1", 26)) + "\n26\n" +
                string.Join('\n', Enumerable.Repeat("2", 26)) + "\n");
            var classifier = new RecordingClassifier(182);
            var pipeline = new LegacyRecognitionPipeline(FeatureNormalizer.Load(normalizerPath), classifier);

            float[] earlyScores = [];
            for (var frame = 0; frame < 6; frame++)
                Assert.False(pipeline.TryClassify(Enumerable.Repeat((float)frame, 26).ToArray(), out earlyScores));

            Assert.Empty(earlyScores);
            Assert.True(pipeline.TryClassify(Enumerable.Repeat(6f, 26).ToArray(), out var scores));
            Assert.Equal([.25f, .75f], scores);
            for (var frame = 0; frame < 7; frame++)
                Assert.All(classifier.LastInput!.AsSpan(frame * 26, 26).ToArray(), value => Assert.Equal((frame - 1) * 2f, value));
        }
        finally
        {
            File.Delete(normalizerPath);
        }
    }

    private sealed class RecordingClassifier(int inputCount) : IClassifier
    {
        public int InputCount => inputCount;
        public int ClassCount => 2;
        public float[]? LastInput { get; private set; }

        public float[] Classify(ReadOnlySpan<float> input)
        {
            LastInput = input.ToArray();
            return [.25f, .75f];
        }
    }
}

public sealed class LegacyFeatureDumpTests
{
    [Fact]
    public void Read_ParsesLittleEndianFramesAndAlignedPitch()
    {
        using var mfcc = Float32Stream(1f, 2f, 3f, 4f);
        using var pitch = Float32Stream(100f, 200f);

        var dump = LegacyFeatureDump.Read(mfcc, pitch, featureCount: 2);

        Assert.Equal([[1f, 2f], [3f, 4f]], dump.MfccFrames);
        Assert.Equal([100f, 200f], dump.Pitch);
    }

    [Fact]
    public void Read_RejectsUnalignedPitch()
    {
        using var mfcc = Float32Stream(1f, 2f);
        using var pitch = Float32Stream(100f, 200f);

        Assert.Throws<InvalidDataException>(() => LegacyFeatureDump.Read(mfcc, pitch, featureCount: 2));
    }

    private static MemoryStream Float32Stream(params float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(index * sizeof(float), sizeof(float)), BitConverter.SingleToInt32Bits(values[index]));
        return new MemoryStream(bytes);
    }
}

public sealed class DeltaFeaturesTests
{
    [Fact]
    public void ComputeCentered_UsesMeasuredPlusMinusTwoFormula()
    {
        float[][] frames = [[0f], [1f], [2f], [3f], [4f]];
        var result = new float[1];

        DeltaFeatures.ComputeCentered(frames, centerFrame: 2, result);

        Assert.Equal(1f, result[0]);
    }

    [Fact]
    public void ComputeCentered_RejectsFramesWithoutBothSidesOfContext()
    {
        float[][] frames = [[0f], [1f], [2f]];

        Assert.Throws<ArgumentOutOfRangeException>(() => DeltaFeatures.ComputeCentered(frames, centerFrame: 1, new float[1]));
    }
}

public sealed class AudioRecognitionRunnerTests
{
    [Theory]
    [InlineData(8_000, 1)]
    [InlineData(16_000, 2)]
    public void Constructor_RejectsAudioOutsideRecoveredProcessingFormat(int sampleRate, int channels)
    {
        var normalizerPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(normalizerPath,
                $"26\n{string.Join('\n', Enumerable.Repeat("0", 26))}\n26\n{string.Join('\n', Enumerable.Repeat("1", 26))}\n");
            var pipeline = new LegacyRecognitionPipeline(
                FeatureNormalizer.Load(normalizerPath), new FixedClassifier());

            Assert.Throws<ArgumentException>(() => new AudioRecognitionRunner(
                new EmptyAudioSource(new AudioFormat(sampleRate, channels)), pipeline));
        }
        finally
        {
            File.Delete(normalizerPath);
        }
    }

    [Fact]
    public async Task RunAsync_JoinsScoresOnlyWithSameTimestampEnergyFrame()
    {
        var normalizerPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(normalizerPath,
                $"26\n{string.Join('\n', Enumerable.Repeat("0", 26))}\n26\n{string.Join('\n', Enumerable.Repeat("1", 26))}\n");
            var source = new ChunkAudioSource(new float[2400]);
            var runner = new AudioRecognitionRunner(source, new LegacyRecognitionPipeline(
                FeatureNormalizer.Load(normalizerPath), new FixedClassifier()));
            var joined = new List<RecognitionWithEnergyResult>();
            runner.RecognitionWithEnergyProduced += (_, result) => joined.Add(result);

            await runner.RunAsync(CancellationToken.None);

            Assert.NotEmpty(joined);
            Assert.All(joined, result =>
            {
                Assert.Equal(0f, result.RootMeanSquareEnergy);
                Assert.Equal(0, result.ZeroCrossings);
                Assert.Equal([1f], result.Scores);
                Assert.Equal(0, result.StartSample % LegacyFrameEnergyStream.HopLength);
            });
        }
        finally
        {
            File.Delete(normalizerPath);
        }
    }

    [Fact]
    public async Task RunAsync_EmitsVoiceActivityForEveryRecoveredEnergyFrame()
    {
        var normalizerPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(normalizerPath,
                $"26\n{string.Join('\n', Enumerable.Repeat("0", 26))}\n26\n{string.Join('\n', Enumerable.Repeat("1", 26))}\n");
            var runner = new AudioRecognitionRunner(new ChunkAudioSource(new float[2400]), new LegacyRecognitionPipeline(
                FeatureNormalizer.Load(normalizerPath), new FixedClassifier()));
            var activity = new List<LegacyVoiceActivityResult>();
            runner.VoiceActivityProduced += (_, result) => activity.Add(result);

            await runner.RunAsync(CancellationToken.None);

            Assert.NotEmpty(activity);
            Assert.All(activity, result =>
            {
                Assert.False(result.IsSpeech);
                Assert.Equal(0, result.StartSample % LegacyFrameEnergyStream.HopLength);
            });
        }
        finally
        {
            File.Delete(normalizerPath);
        }
    }

    private sealed class FixedClassifier : IClassifier
    {
        public int InputCount => 182;
        public int ClassCount => 1;
        public float[] Classify(ReadOnlySpan<float> input) => [1f];
    }

    private sealed class EmptyAudioSource(AudioFormat format) : IAudioSource
    {
        public AudioFormat Format => format;
        public async IAsyncEnumerable<AudioChunk> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ChunkAudioSource(float[] samples) : IAudioSource
    {
        public AudioFormat Format { get; } = new(16_000, 1);
        public async IAsyncEnumerable<AudioChunk> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AudioChunk(0, samples);
            await Task.CompletedTask.ConfigureAwait(false);
        }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class LegacyDirectionMapperTests
{
    [Theory]
    [InlineData(0, -1f, 0f)]
    [InlineData(1, 0f, 1f)]
    [InlineData(2, 1f, 0f)]
    [InlineData(3, 0f, -1f)]
    public void Map_FourClassLayoutUsesRecoveredCardinalOrder(int activeClass, float expectedY, float expectedX)
    {
        var probabilities = new float[4];
        probabilities[activeClass] = 1f;

        var direction = new LegacyDirectionMapper(4, 0f).Map(probabilities);

        Assert.Equal(expectedX, direction.X, precision: 6);
        Assert.Equal(expectedY, direction.Y, precision: 6);
        Assert.Equal(2f, direction.Magnitude, precision: 6);
    }

    [Fact]
    public void Map_EightClassLayoutAcceptsDirectUpperRightDiagonal()
    {
        var direction = new LegacyDirectionMapper(8, 0f).Map([0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f]);

        Assert.Equal(MathF.Sqrt(.5f), direction.X, precision: 6);
        Assert.Equal(-MathF.Sqrt(.5f), direction.Y, precision: 6);
    }

    [Fact]
    public void Map_AppliesProbabilityThresholdBeforeMixing()
    {
        var direction = new LegacyDirectionMapper(4, .7f).Map([.7f, .7f, .7f, .7f]);

        Assert.Equal(LegacyDirection.None, direction);
    }

    [Fact]
    public void Map_FifthClassIsNeutralInRecoveredFiveClassLayout()
    {
        var direction = new LegacyDirectionMapper(5, 0f).Map([0f, 0f, 0f, 0f, 1f]);

        Assert.Equal(LegacyDirection.None, direction);
    }
}

public sealed class LegacyVelocityMapperTests
{
    [Fact]
    public void Map_PerAxisModeUsesRecoveredCardinalEnergyAggregation()
    {
        var mapper = new LegacyVelocityMapper(new LegacyVelocityConfiguration(
            classCount: 4, probabilityThreshold: 0f,
            powerReferences: [1f, 1f, 1f, 1f], slopes: [1f, 1f, 1f, 1f], intercepts: [0f, 0f, 0f, 0f],
            mode: LegacyVelocityMode.PerAxis, beta: 1f, gamma: 0f, alpha: 1f, offset: 0f));

        var velocity = mapper.Map(2f, [.2f, .3f, .4f, .5f]);

        Assert.Equal(1.6f, velocity.X, precision: 6);
        Assert.Equal(1.2f, velocity.Y, precision: 6);
        Assert.Equal(2.8f, velocity.Global, precision: 6);
    }

    [Fact]
    public void Map_SingleModeUsesOneGlobalVelocityForBothAxes()
    {
        var mapper = new LegacyVelocityMapper(new LegacyVelocityConfiguration(
            classCount: 4, probabilityThreshold: 0f,
            powerReferences: [1f, 1f, 1f, 1f], slopes: [1f, 1f, 1f, 1f], intercepts: [0f, 0f, 0f, 0f],
            mode: LegacyVelocityMode.Single, beta: 1f, gamma: 0f, alpha: 1f, offset: 0f));

        var velocity = mapper.Map(2f, [.2f, .3f, .4f, .5f]);

        Assert.Equal(2.8f, velocity.X, precision: 6);
        Assert.Equal(2.8f, velocity.Y, precision: 6);
        Assert.Equal(2.8f, velocity.Global, precision: 6);
    }

    [Fact]
    public void Map_ZeroesProbabilitiesAtTheLegacyThreshold()
    {
        var mapper = new LegacyVelocityMapper(new LegacyVelocityConfiguration(
            classCount: 4, probabilityThreshold: .7f,
            powerReferences: [1f, 1f, 1f, 1f], slopes: [1f, 1f, 1f, 1f], intercepts: [0f, 0f, 0f, 0f],
            mode: LegacyVelocityMode.Single, beta: 1f, gamma: 0f, alpha: 1f, offset: 0f));

        Assert.Equal(new LegacyVelocity(0f, 0f, 0f), mapper.Map(1f, [.7f, .7f, .7f, .7f]));
    }
}

public sealed class LegacyMotionControllerTests
{
    [Fact]
    public void Update_AppliesAxisVelocityToRecoveredRawDirection()
    {
        var controller = CreateController(axisDeadZone: 0f, maximumAxisVelocity: 10f);

        var result = controller.Update(.5f, [0f, 1f, 0f, 0f]);

        Assert.Equal(1f, result.XDirection, precision: 6);
        Assert.Equal(0f, result.YDirection, precision: 6);
        Assert.Equal(1f, result.Velocity, precision: 6);
        Assert.Equal(.5f, result.GlobalVelocity, precision: 6);
    }

    [Fact]
    public void Update_DeadZonesThenClampsBeforeFinalNormalisation()
    {
        var deadZoned = CreateController(axisDeadZone: .2f, maximumAxisVelocity: 10f)
            .Update(.05f, [0f, 1f, 0f, 0f]);
        var clamped = CreateController(axisDeadZone: 0f, maximumAxisVelocity: 2f)
            .Update(10f, [0f, 1f, 0f, 0f]);

        Assert.Equal(0f, deadZoned.Velocity);
        Assert.Equal(2f, clamped.Velocity, precision: 6);
        Assert.Equal(1f, clamped.XDirection, precision: 6);
    }

    private static LegacyMotionController CreateController(float axisDeadZone, float maximumAxisVelocity)
    {
        var directions = new LegacyDirectionMapper(4, 0f);
        var velocities = new LegacyVelocityMapper(new LegacyVelocityConfiguration(
            classCount: 4, probabilityThreshold: 0f,
            powerReferences: [1f, 1f, 1f, 1f], slopes: [1f, 1f, 1f, 1f], intercepts: [0f, 0f, 0f, 0f],
            mode: LegacyVelocityMode.PerAxis, beta: 1f, gamma: 0f, alpha: 1f, offset: 0f));
        return new LegacyMotionController(directions, velocities, axisDeadZone, maximumAxisVelocity);
    }
}

public sealed class LegacyMotionCalibrationTests
{
    [Fact]
    public void Load_ReadsProfilePowerAndLeadingLinearRegressionPairs()
    {
        var powerPath = Path.GetTempFileName();
        var regressionPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(powerPath, "10\n20\n30\n40\n");
            File.WriteAllText(regressionPath, "# slope bias\n1 .1\n2 .2\n3 .3\n4 .4\n5 .5\n");

            var calibration = LegacyMotionCalibration.Load(powerPath, regressionPath, 4);

            Assert.Equal([10f, 20f, 30f, 40f], calibration.PowerReferences);
            Assert.Equal([1f, 2f, 3f, 4f], calibration.Slopes);
            Assert.Equal([.1f, .2f, .3f, .4f], calibration.Intercepts);
        }
        finally
        {
            File.Delete(powerPath);
            File.Delete(regressionPath);
        }
    }
}

public sealed class LegacyMotionParametersTests
{
    [Fact]
    public void Load_ReadsBundledMotionSettingsAndUsesRecoveredAlphaDefault()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                --velMode 1
                --beta 0.5
                --gamma 1.0
                --offset 0.0
                --maxvel 10.0
                --pthresh 0.7
                --vthresh 0.25
                --doClick
                """);

            var parameters = LegacyMotionParameters.Load(path);

            Assert.Equal(LegacyVelocityMode.PerAxis, parameters.VelocityMode);
            Assert.Equal(.7f, parameters.ProbabilityThreshold);
            Assert.Equal(.5f, parameters.Beta);
            Assert.Equal(1f, parameters.Gamma);
            Assert.Equal(1f, parameters.Alpha);
            Assert.Equal(10f, parameters.MaximumVelocity);
            Assert.Equal(.25f, parameters.AxisDeadZone);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Factory_LoadsCalibrationAndBuildsControllerWithoutCursorAccess()
    {
        var parameterPath = Path.GetTempFileName();
        var powerPath = Path.GetTempFileName();
        var regressionPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(parameterPath, "--velMode 1\n--beta 1\n--gamma 0\n--offset 0\n--maxvel 10\n--pthresh 0\n");
            File.WriteAllText(powerPath, "1\n1\n1\n1\n");
            File.WriteAllText(regressionPath, "1 0\n1 0\n1 0\n1 0\n");

            var controller = LegacyMotionControllerFactory.Create(parameterPath, powerPath, regressionPath, 4, axisDeadZone: 0f);
            var result = controller.Update(.5f, [0f, 1f, 0f, 0f]);

            Assert.Equal(1f, result.XDirection, precision: 6);
            Assert.Equal(0f, result.YDirection, precision: 6);
            // The recovered cardinal direction has pre-normalisation
            // magnitude 2, which the legacy caller applies before its final
            // normalisation step.
            Assert.Equal(1f, result.Velocity, precision: 6);
        }
        finally
        {
            File.Delete(parameterPath);
            File.Delete(powerPath);
            File.Delete(regressionPath);
        }
    }

    [Fact]
    public void Load_UsesRecoveredOneMicroUnitDeadZoneWhenVthreshIsOmitted()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "--velMode 0\n--beta 1\n--gamma 0\n--offset 0\n--maxvel 1\n--pthresh 0\n");

            var parameters = LegacyMotionParameters.Load(path);

            Assert.Equal(.000001f, parameters.AxisDeadZone);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class LegacyVoiceActivityDetectorTests
{
    [Fact]
    public void Configuration_LoadsSuppliedVstartAndRecoveredDefaults()
    {
        var configuration = LegacyVoiceActivityConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "legacy", "prm.cfg"));

        Assert.Equal(3f, configuration.VoiceStartMultiplier);
        Assert.Equal(2f, configuration.VoiceSustainMultiplier);
        Assert.Equal(20f, configuration.EnergyFloor);
        Assert.Equal(300, configuration.NoiseWindowFrameCount);
        Assert.Equal(5, configuration.MinimumSilenceFrameCount);
    }

    [Fact]
    public void Push_UsesStartAndSustainThresholdsThenEndsAfterMinimumSilence()
    {
        var detector = new LegacyVoiceActivityDetector(new LegacyVoiceActivityConfiguration(
            VoiceStartMultiplier: 2f, VoiceSustainMultiplier: 1f, EnergyFloor: 0f,
            ZeroCrossingFloor: 1, LowPassPole: 0f, NoiseStandardDeviationLimit: 1f,
            NoiseWindowFrameCount: 3, MinimumSilenceFrameCount: 1, NoiseUpdateMinimumDifference: 1f));

        var quiet = detector.Push(new LegacyEnergyFrame(0, 100f, 2));
        var started = detector.Push(new LegacyEnergyFrame(160, 250f, 2));
        var held = detector.Push(new LegacyEnergyFrame(320, 50f, 2));
        var ended = detector.Push(new LegacyEnergyFrame(480, 50f, 2));

        Assert.False(quiet.IsSpeech);
        Assert.True(started.StartedSpeech);
        Assert.True(held.IsSpeech);
        Assert.True(ended.StartedSilence);
        Assert.Equal(1, ended.CompletedSpeechFrameCount);
    }

    [Fact]
    public void Push_UpdatesNoiseOnlyAfterAStableFullWindow()
    {
        var detector = new LegacyVoiceActivityDetector(new LegacyVoiceActivityConfiguration(
            VoiceStartMultiplier: 10f, VoiceSustainMultiplier: 10f, EnergyFloor: 0f,
            ZeroCrossingFloor: 0, LowPassPole: 0f, NoiseStandardDeviationLimit: .1f,
            NoiseWindowFrameCount: 3, MinimumSilenceFrameCount: 0, NoiseUpdateMinimumDifference: 10f));

        detector.Push(new LegacyEnergyFrame(0, 100f, 0));
        detector.Push(new LegacyEnergyFrame(160, 130f, 0));
        detector.Push(new LegacyEnergyFrame(320, 130f, 0));
        var updated = detector.Push(new LegacyEnergyFrame(480, 130f, 0));

        Assert.Equal(130f, updated.NoisePower);
    }
}

public sealed class LegacyCommandModelTests
{
    [Fact]
    public void CommandScorer_AcceptBestKeepsFirstDictionaryHypothesisOnProbabilityTie()
    {
        var first = new LegacyCommandScore(new LegacyCommandPronunciation(1, "first", "first", [], []), new LegacyEbmmViterbiResult(0f, []))
        {
            RelativeProbability = .5f
        };
        var second = new LegacyCommandScore(new LegacyCommandPronunciation(2, "second", "second", [], []), new LegacyEbmmViterbiResult(0f, []))
        {
            RelativeProbability = .5f
        };

        var accepted = LegacyCommandScorer.AcceptBest([first, second], .5f);

        Assert.Equal(1, accepted.AcceptedScore!.Pronunciation.Id);
    }

    [Fact]
    public void GaussianMixture_LoadsDiagonalComponentsAndComputesLogProbability()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                2 # features
                2 # mixtures
                0
                1
                0
                1
                0 0
                0
                1 1
                1
                1
                0
                1
                1 1
                0
                1 1
                0 # BMM count
                """);

            var mixtures = LegacyGaussianMixtureCollection.Load(path);

            Assert.Equal(2, mixtures.FeatureCount);
            Assert.Equal(2, mixtures.MixtureCount);
            Assert.Equal(-MathF.Log(2f * MathF.PI), mixtures[0].LogProbability([0f, 0f]), precision: 5);
            Assert.True(mixtures[0].LogProbability([0f, 0f]) > mixtures[1].LogProbability([0f, 0f]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CommandDictionary_LoadsPronunciationsAndTheirStatePaths()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                EBMM_MPF
                2
                0 k k
                3
                -1 36 -2
                0 1 1 1
                1 1 2 1
                2 0
                1 ch C
                3
                -1 40 -2
                0 1 1 1
                1 1 2 1
                2 0
                """);

            var dictionary = LegacyCommandDictionary.Load(path);

            Assert.Collection(dictionary.Pronunciations,
                first => { Assert.Equal(0, first.Id); Assert.Equal("k", first.Orthography); Assert.Equal([36], first.BmmIds); Assert.Equal(new LegacyEbmmTransition(1, 1f), Assert.Single(first.States[0].Transitions)); },
                second => { Assert.Equal(1, second.Id); Assert.Equal("ch", second.Orthography); Assert.Equal("C", second.Transcription); });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GaussianMixture_LoadsTheSuppliedLegacyAcousticCollection()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "legacy", "vj.hmmf");

        var mixtures = LegacyGaussianMixtureCollection.Load(path);

        Assert.Equal(26, mixtures.FeatureCount);
        Assert.Equal(126, mixtures.MixtureCount);
        Assert.Equal(12, mixtures[0].ComponentCount);
        Assert.Equal(42, mixtures.BmmCount);
        Assert.Equal([108, 109, 110], mixtures.GetBmm(36).EmissionMixtureIds);
        Assert.Equal([120, 121, 122], mixtures.GetBmm(40).EmissionMixtureIds);
    }

    [Fact]
    public void CommandDictionary_LoadsTheSuppliedTwoLegacyWordGraphs()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "legacy", "vj.dict");

        var dictionary = LegacyCommandDictionary.Load(path);

        Assert.Collection(dictionary.Pronunciations,
            k => { Assert.Equal("k", k.Orthography); Assert.Equal([0, 36, 0], k.BmmIds); Assert.Equal(5, k.States.Count); },
            ch => { Assert.Equal("ch", ch.Orthography); Assert.Equal([0, 40, 0], ch.BmmIds); Assert.Equal(5, ch.States.Count); });
    }

    [Fact]
    public void BmmViterbi_DecodesALeftToRightEmissionPath()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                1
                1
                0
                1
                0
                1
                0
                0
                1
                1
                0
                3
                0
                0 1 0
                .5 .5
                0
                0
                """);
            var acoustic = LegacyGaussianMixtureCollection.Load(path);

            var result = LegacyBmmViterbiDecoder.Decode(acoustic, acoustic.GetBmm(0), [[0f], [0f]]);

            var gaussianLogProbability = -.5f * MathF.Log(2f * MathF.PI);
            Assert.Equal(2f * gaussianLogProbability + 2f * MathF.Log(.5f), result.LogScore, precision: 5);
            Assert.Equal([1, 1], result.EmittingStatePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EbmmViterbi_ComposesConsecutiveBmmModels()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                1
                1
                0
                1
                0
                1
                0
                0
                1
                2
                0
                3
                0
                0 1 0
                .5 .5
                0
                0
                1
                3
                0
                0 1 0
                .5 .5
                0
                0
                """);
            var acoustic = LegacyGaussianMixtureCollection.Load(path);
            var pronunciation = new LegacyCommandPronunciation(
                0, "test", "test", [0, 1],
                [
                    new LegacyEbmmState(0, [new LegacyEbmmTransition(1, 1f)]),
                    new LegacyEbmmState(1, [new LegacyEbmmTransition(2, 1f)]),
                    new LegacyEbmmState(2, [new LegacyEbmmTransition(3, 1f)]),
                    new LegacyEbmmState(3, [])
                ]);

            var result = LegacyEbmmViterbiDecoder.Decode(acoustic, pronunciation, [[0f], [0f]]);

            var gaussianLogProbability = -.5f * MathF.Log(2f * MathF.PI);
            Assert.Equal(2f * gaussianLogProbability + 2f * MathF.Log(.5f), result.LogScore, precision: 5);
            Assert.Equal([1, 2], result.FramePath.Select(frame => frame.EbmmStateIndex));
            Assert.Equal([0, 1], result.FramePath.Select(frame => frame.BmmId));
            Assert.Equal([1, 1], result.FramePath.Select(frame => frame.BmmEmittingState));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CommandScorer_RanksDictionaryEntriesWithoutAcceptingACommand()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                1
                2
                0
                1
                0
                1
                0
                0
                1
                1
                1
                0
                1
                5
                0
                1
                2
                0
                3
                0
                0 1 0
                0 1
                0
                0
                1
                3
                0
                0 1 0
                0 1
                0
                1
                """);
            var acoustic = LegacyGaussianMixtureCollection.Load(path);
            var dictionary = new LegacyCommandDictionary(
            [
                new LegacyCommandPronunciation(1, "ch", "C", [1],
                    [new LegacyEbmmState(0, [new LegacyEbmmTransition(1, 1f)]), new LegacyEbmmState(1, [new LegacyEbmmTransition(2, 1f)]), new LegacyEbmmState(2, [])]),
                new LegacyCommandPronunciation(0, "k", "k", [0],
                    [new LegacyEbmmState(0, [new LegacyEbmmTransition(1, 1f)]), new LegacyEbmmState(1, [new LegacyEbmmTransition(2, 1f)]), new LegacyEbmmState(2, [])])
            ]);

            var ranked = LegacyCommandScorer.ScoreAll(acoustic, dictionary, [[0f]]);

            // ScoreAll preserves legacy dictionary order; selecting the best
            // hypothesis is a separate strict-greater scan.
            Assert.Equal(["ch", "k"], ranked.Select(score => score.Pronunciation.Orthography));
            Assert.All(ranked, score => Assert.Single(score.Viterbi.FramePath));
            Assert.True(ranked[0].RelativeProbability < .01f);
            Assert.True(ranked[1].RelativeProbability > .99f);
            Assert.Equal("k", LegacyCommandScorer.AcceptBest(ranked, .7f).AcceptedScore!.Pronunciation.Orthography);
            Assert.False(LegacyCommandScorer.AcceptBest(ranked, 1f).IsAccepted);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DiscreteSegmenter_AcceptsOnlyACompleteLegacyLengthAndPreconditionedSegment()
    {
        var segmenter = new LegacyDiscreteCommandSegmenter(new LegacyDiscreteSegmentationConfig(
            MinimumFrameCount: 2, MaximumFrameCountExclusive: 4,
            MinimumInitialUnvoicedFrameCount: 0, MinimumAverageZeroCrossings: .5f));
        segmenter.StartOfSpeech();
        segmenter.Push(new LegacyDiscreteFeatureFrame(new float[26], 1, IsVoiced: false));
        segmenter.Push(new LegacyDiscreteFeatureFrame(new float[26], 2, IsVoiced: true));

        var result = segmenter.StartOfSilence();

        Assert.True(result.IsAccepted);
        Assert.Equal(LegacyDiscreteSegmentRejection.None, result.Rejection);
        Assert.Equal(2, result.Segment!.Features.Count);
        Assert.Equal(1.5f, result.Segment.AverageZeroCrossings);
        Assert.Equal(1, result.Segment.InitialUnvoicedFrameCount);
    }

    [Fact]
    public void DiscreteSegmenter_RejectsAFragmentAtTheExclusiveLegacyMaximum()
    {
        var segmenter = new LegacyDiscreteCommandSegmenter(new LegacyDiscreteSegmentationConfig(
            MinimumFrameCount: 2, MaximumFrameCountExclusive: 3,
            MinimumInitialUnvoicedFrameCount: 0, MinimumAverageZeroCrossings: 0f));
        segmenter.StartOfSpeech();
        segmenter.Push(new LegacyDiscreteFeatureFrame(new float[26], 1, IsVoiced: false));
        segmenter.Push(new LegacyDiscreteFeatureFrame(new float[26], 1, IsVoiced: true));
        segmenter.Push(new LegacyDiscreteFeatureFrame(new float[26], 1, IsVoiced: true));

        var result = segmenter.StartOfSilence();

        Assert.False(result.IsAccepted);
        Assert.Equal(LegacyDiscreteSegmentRejection.TooLong, result.Rejection);
    }

    [Fact]
    public void DiscreteCommandRecognizer_ComposesExplicitSpeechBoundariesWithoutSideEffects()
    {
        var acoustic = LegacyGaussianMixtureCollection.Load(Path.Combine(AppContext.BaseDirectory, "legacy", "vj.hmmf"));
        var dictionary = LegacyCommandDictionary.Load(Path.Combine(AppContext.BaseDirectory, "legacy", "vj.dict"));
        var recognizer = new LegacyDiscreteCommandRecognizer(acoustic, dictionary,
            new LegacyDiscreteSegmentationConfig(2, 4, 0, 0f), 0f);
        recognizer.StartOfSpeech();
        recognizer.Push(new LegacyDiscreteFeatureFrame(new float[26], 1, IsVoiced: false));
        recognizer.Push(new LegacyDiscreteFeatureFrame(new float[26], 1, IsVoiced: true));
        recognizer.Push(new LegacyDiscreteFeatureFrame(new float[26], 1, IsVoiced: true));

        var result = recognizer.StartOfSilence();

        Assert.True(result.Segmentation.IsAccepted);
        Assert.True(result.IsAccepted);
        Assert.Equal(2, result.RankedScores.Count);
        Assert.NotNull(result.Acceptance!.AcceptedScore);
    }

    [Fact]
    public void DiscreteCommandParameters_LoadsTheSuppliedLegacyThresholds()
    {
        var parameters = LegacyDiscreteCommandParameters.Load(Path.Combine(AppContext.BaseDirectory, "legacy", "prm.cfg"));

        Assert.Equal(.7f, parameters.ProbabilityThreshold);
        Assert.Equal(.5f, parameters.MinimumAverageZeroCrossings);
        var segmentation = parameters.CreateSegmentationConfig();
        Assert.Equal(5, segmentation.MinimumFrameCount);
        Assert.Equal(70, segmentation.MaximumFrameCountExclusive);
    }
}

public sealed class CursorSafetyGateTests
{
    [Fact]
    public void NamedCommandAction_UsesClassIndependentActionIdAndSafetyGate()
    {
        var gate = new CursorSafetyGate();
        var output = new RecordingMouseOutput();
        var router = new GatedCommandActionRouter(gate, output);

        var blocked = router.Dispatch("right_click");
        gate.Enable();
        var dispatched = router.Dispatch("right_click");

        Assert.True(blocked.IsMapped);
        Assert.False(blocked.Executed);
        Assert.True(dispatched.Executed);
        Assert.Equal([MouseButtonAction.RightClick], output.Actions);
    }

    [Fact]
    public void TryMove_RequiresExplicitEnableAndEmergencyStopDisablesAgain()
    {
        var gate = new CursorSafetyGate();
        var output = new RecordingCursorOutput();

        Assert.False(gate.TryMove(output, new CursorDelta(3, -2)));
        gate.Enable();
        Assert.True(gate.TryMove(output, new CursorDelta(3, -2)));
        gate.EmergencyStop();
        Assert.False(gate.TryMove(output, new CursorDelta(1, 1)));

        Assert.Equal([new CursorDelta(3, -2)], output.Moves);
        Assert.False(gate.IsEnabled);
    }

    [Fact]
    public void Quantize_PreservesSubpixelResidualUntilItBecomesWholePixel()
    {
        var quantizer = new MotionDeltaQuantizer(.5f);
        var motion = new LegacyMotionSample(1f, 0f, 1f, 1f);

        Assert.Equal(new CursorDelta(0, 0), quantizer.Quantize(motion));
        Assert.Equal(new CursorDelta(1, 0), quantizer.Quantize(motion));
        quantizer.Reset();
        Assert.Equal(new CursorDelta(0, 0), quantizer.Quantize(motion));
    }

    private sealed class RecordingCursorOutput : ICursorOutput
    {
        public List<CursorDelta> Moves { get; } = [];
        public void MoveBy(CursorDelta delta) => Moves.Add(delta);
    }

    private sealed class RecordingMouseOutput : IMouseActionOutput
    {
        public List<MouseButtonAction> Actions { get; } = [];
        public void Click(MouseButtonAction action) => Actions.Add(action);
    }
}

public sealed class VoiceCursorCoordinatorTests
{
    [Fact]
    public void Process_CalculatesWhilePausedButAppliesOnlyAfterExplicitEnable()
    {
        var gate = new CursorSafetyGate();
        var output = new RecordingCursorOutput();
        var coordinator = new VoiceCursorCoordinator(CreateMotionController(), new MotionDeltaQuantizer(1f), gate, output);
        var recognition = new RecognitionWithEnergyResult(1280, .5f, 0, [0f, 1f, 0f, 0f]);

        var paused = coordinator.Process(recognition);
        gate.Enable();
        var enabled = coordinator.Process(recognition);
        coordinator.EmergencyStop();
        var stopped = coordinator.Process(recognition);

        Assert.Equal(1f, paused.Motion.XDirection, precision: 6);
        Assert.False(paused.AppliedToCursor);
        Assert.True(enabled.AppliedToCursor);
        Assert.False(stopped.AppliedToCursor);
        Assert.Equal([new CursorDelta(1, 0)], output.Moves);
    }

    private static LegacyMotionController CreateMotionController()
    {
        var velocities = new LegacyVelocityMapper(new LegacyVelocityConfiguration(
            classCount: 4, probabilityThreshold: 0f,
            powerReferences: [1f, 1f, 1f, 1f], slopes: [1f, 1f, 1f, 1f], intercepts: [0f, 0f, 0f, 0f],
            mode: LegacyVelocityMode.PerAxis, beta: 1f, gamma: 0f, alpha: 1f, offset: 0f));
        return new LegacyMotionController(new LegacyDirectionMapper(4, 0f), velocities, 0f, 10f);
    }

    private sealed class RecordingCursorOutput : ICursorOutput
    {
        public List<CursorDelta> Moves { get; } = [];
        public void MoveBy(CursorDelta delta) => Moves.Add(delta);
    }
}
