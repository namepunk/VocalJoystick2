# Legacy analysis log

## Known

- The installation is a 32-bit Windows legacy distribution using the VC90
  runtime; the new application must not depend on it.
- `prm.cfg` selects classifier mode 2, confirmed by `libvj.dll` strings as
  ML-RSC.
- Feature-model cardinalities are 26 features and a 182-value context input,
  i.e. seven 26-feature frames. `FUN_100369c0`, the classifier caller,
  copies circular-buffer offsets `-8` through `-2` in that increasing order;
  the model input is therefore oldest frame first and newest frame last.
- `vj.norms` defines 26 means and 26 inverse standard deviations.
- The MLP `.wts` model format is completely enumerable and its dimensions are
  validated by `tools/model_inspector.py`.
- Static analysis of `mlpNNAdapt` establishes row-major output-neuron matrix
  orientation, the decreasing hidden sigmoid `1 / (1 + exp(x))`, and naive
  softmax output normalization.
- The ML-RSC and RSC headers are binary little-endian as described in
  `model-formats.md`; the remainder is float32-aligned. For ML-RSC, the exact
  four payload blocks, decreasing-sigmoid hidden layer, class score, and score
  normalization are now recovered and validated by `tools/model_inspector.py`
  and `LegacyMlrscModel` tests.
- The discrete command graph has two entries, `k` and `ch`, and the acoustic
  collection uses 26-dimensional mixtures.
- `adapt.bat` keeps base models separate from per-user output models.
- On the microphone input path, `VJEngine::Reset` configures PortAudio for
  16 kHz mono. The frame extractor uses a 960-sample (60 ms) frame with a
  160-sample (10 ms) hop, while static MFCC extraction consumes 400 samples
  (25 ms).

## Likely, not yet an implementation contract

- The 182-value MLP/ML-RSC/RSC input is seven adjacent normalized 26-value
  frames. The ordering is recovered (oldest to newest); exact start-of-stream
  and circular-buffer seeding policy still require a reference run.
- `FeatureExtractor`, `FrameExtractor`, and `FrameFeaturesCalculator` form the
  audio-to-feature pipeline. Exported names show power, zero-crossing, pitch,
  autocorrelation, center clipping, and a Hamming operation.
- `PatternExtractor` is responsible for speech/silence events and optionally
  applies a Kalman filter (`doKalman`, `filterCoeff`).
- `MotionExtractor` maps recognized pattern data to `MotionData`, which holds
  direction, velocity, energy, pitch, and vowel probabilities.

## Unknown

- Exact velocity aggregation, temporal filtering, and the mapping from its
  floating-point values to operating-system cursor steps.
- The legacy pitch/voicing value that `PatternExtractor` supplies alongside a
  completed 26-value feature frame. It must not be inferred from VAD state.
- GMM likelihood normalization and BMM/EBMM Viterbi boundaries.

## 2026-09-24: discrete-command path status

Static analysis now establishes the model-side contract without yet supplying
an unsafe guessed live-segmentation rule:

- `vj.dict` paths are `k: [0, 36, 0]` and `ch: [0, 40, 0]`, not single BMMs.
  `EBMM::read` provides an explicit left-to-right transition graph around
  those BMM states.
- `EBMM::viterbiDecode` has a direct static call only from `hmmadapt`; the
  live path reaches model operations through virtual dispatch, so direct-call
  xrefs alone do not identify the runtime command boundary or acceptance
  threshold.
- `PatternExtractor::frameFeaturesDataReceived` keeps a five-frame delayed
  feature reference, uses `FeatureExtractor::pitchBackTracking`, accumulates
  zero-crossing information for unpitched frames, and reads the parameter at
  the `minzcross` configuration offset before signalling its next consumer.
- `KeyboardController` consumes completed `PatternData` carrying an already
  assigned discrete identifier. It is therefore downstream of HMM acceptance,
  not a substitute for it.

Further recovery of `PatternExtractor::startOfSilence` and its classifier now
establishes the discrete-sound acceptance order:

1. A fragment is considered only on a transition to silence, with legacy
   length `5 <= frames < 70` (the recovered defaults).
2. It rejects insufficient average zero crossings and an initial unvoiced
   run not longer than the configured minimum (default one frame).
3. Every dictionary EBMM receives the same 26-value feature sequence through
   `vitInference`. The legacy code exponentiates those Viterbi log scores,
   normalizes across words, and rejects the best word below `pthresh`.

The recovered `PatternExtractor::startOfSilence` path now confirms the precise
order before HMM scoring: it accepts only `5 <= frames < 70`, copies every
26-value feature frame into the discrete buffer, and independently counts
leading frames with `pitch <= 0` plus average zero crossings. The classifier
is invoked only when both strict tests pass: `averageZeroCrossings > minzcross`
and `leadingUnvoicedFrames > minDiscUnvoicedLength`. This matches the existing
managed segmenter comparisons. `DISC_UNVOICED_BIAS`, `DISC_UNVOICED_SLOPE`,
and `DISC_UNVOICED_LENGTH` are not read by either recovered helper; their
active consumer remains to be traced.

The post-HMM helper loops over dictionary EBMMs in source order, applies
`vitInference`, then performs direct `exp(score) / sum(exp(scores))`
normalization. It updates the winner only for a strictly larger probability,
so equal probabilities retain the first dictionary entry, and rejects only
when `bestProbability < pthresh`.

The modern `LegacyDiscreteCommandSegmenter` still exposes those boundary checks
only after explicit start-of-speech/end-of-speech calls. The VAD now emits the
recovered transitions as diagnostics, but is not yet allowed to drive command
recognition.

The DLL also has a distinct continuous-sound path. After five frames of pitch
backtracking it publishes a continuous feature only when the pitch is voiced
and `unvoicedZeroCrossings / (unvoicedFrames + 1) < VOWEL_ZEROCROSS_CEIL`
(parameter offset `0x2b8`). Its pending classifier state is reset when
silence begins. This path is not part of the discrete EBMM command decision,
and is not yet connected to the modern application. `unvBias`, `unvSlope`,
and `unvLen` still have no recovered consumer, so they deliberately remain
inactive rather than being guessed into either path.

The profile names `unvBias`, `unvSlope`, and `unvLen` do not configure pitch.
`ParametersClass::setCmdArgVals` maps them to `DISC_UNVOICED_BIAS`,
`DISC_UNVOICED_SLOPE`, and `DISC_UNVOICED_LENGTH` respectively. They are part
of the discrete-command branch and must be recovered there before claiming a
fully compatible command segmenter. `f0Mode` alone maps to the pitch mode.

`FeatureExtractor::audioFrameReceived` is now identified as that preceding
state machine. Per 60 ms / 10 ms frame it smooths power with `lpfpole`, keeps
noise power, and combines power relative to noise with a zero-crossing floor.
It emits `StartOfSpeech` only on a silence-to-speech transition and
`StartOfSilence` only on the reverse transition, while treating short silent
runs specially for noise re-estimation. The recovered code maps the start
threshold to `vstart * noisePower + efloor` and uses the corresponding
sustained-voice threshold with `vend`.

Direct memory inspection of `ParametersClass::setDefaults` resolves those
defaults: `vstart=5`, `vend=2`, `efloor=20`, `zcfloor=3`, `lpfpole=0.65`,
`noiseDiff=80`, `noiseReest=20`, `maxNoSpch=300`, `powerDrop=0.3`, and
`minsil=5`. The supplied profile overrides `vstart` to `3`; it sets the
discrete `pthresh` to `0.7` and `minzcross` to `0.5` (their defaults are
`0.85` and `0.5`). The recovered noise window holds 300 non-speech frames;
when full and sufficiently stable, it replaces noise power only if the mean
differs by more than 10.

The managed implementation now has read-only GMM/BMM/EBMM Viterbi scoring,
dictionary ranking, and a VAD diagnostic event. It intentionally has no live
command event or keyboard/mouse output until the feature-to-segment bridge is
recovered and tested.

## 2026-09-24: recovered pitch/voicing structure

`PatternExtractor::frameFeaturesDataReceived` confirms that voicing is not
VAD state. It takes the feature frame five positions behind the current frame,
then calls `FeatureExtractor::pitchBackTracking` across that five-frame delay.
The returned positive pitch marks a voiced frame; a dedicated extra candidate
represents no pitch.

The pitch path is now structurally recovered:

- it calculates normalized cross-correlation over lag candidates 32 through
  319 at 16 kHz (approximately 50–500 Hz), from the full 960-sample input
  frame rather than the 400-sample MFCC subframe;
- it constructs lag candidates 32 through 319 (inclusive), computes local
  candidate costs using a tenth-order prediction/error stage, sorts candidates
  by ascending cost, and keeps the configured number for each frame;
- a dynamic-programming stage chooses a path across frames using transition
  costs; backtracking writes the selected candidate into each frame;
- a selected lag becomes pitch via a DLL constant divided by the lag. The
  dedicated no-pitch candidate writes `-0.1`, which the consumer treats as
  unvoiced (`pitch <= 0`).

This is enough to rule out a substitute based on VAD or RMS. The exact
normalized-correlation arithmetic, pitch scale and candidate-count
configuration still need a faithful managed port and a Windows XP reference
trace before the result may be supplied to discrete-command recognition.

The XP pitch captures provide an empirical scale check even though they do
not reveal the chosen candidate path: `16000 / 36 = 444.444...` matches the
440 Hz capture's early pitch, `16000 / 109 = 146.789...` matches its later
subharmonic, and `16000 / 300 = 53.333...` matches the low candidate observed
in the 2 kHz capture. This supports a 16 kHz lag-to-pitch numerator; it does
not recover candidate costs or dynamic-programming choices. The managed live
command bridge therefore currently uses a documented periodicity-based
voicing approximation rather than claiming an exact legacy pitch port.

## 2026-09-24: recovered direction portion of `MotionExtractor`

Static analysis of exported `MotionExtractor::patternDataReceived`
(`0x1003b5d0`) and its helper (`0x100246e0`) establishes the direction mapping
without depending on the paper:

- Each class probability at or below the legacy vowel threshold is zeroed
  before mixing.
- Classes `0..3` correspond, in order, to up, right, down, left.
- With a four-class model, the DLL synthesizes the diagonal values as
  `(up + right) / sqrt(2)`, `(right + down) / sqrt(2)`,
  `(down + left) / sqrt(2)`, and `(left + up) / sqrt(2)`.
- With eight classes, slots `4..7` are already the upper-right,
  lower-right, lower-left, and upper-left diagonals. Five- and nine-class
  layouts reserve the final class as neutral for direction purposes.
- It calculates raw axes as
  `x = right - left + (upperRight + lowerRight - lowerLeft - upperLeft) / sqrt(2)`
  and
  `y = down - up + (lowerLeft + lowerRight - upperRight - upperLeft) / sqrt(2)`,
  then normalizes the nonzero vector.

`LegacyDirectionMapper` implements precisely this recovered stage, but does
not yet issue mouse input.

The adjacent velocity helper (`0x10024400`) is also now recovered:

- For each class it computes
  `s_i = max(0, (energy / powerReference_i * slope_i + intercept_i) * probability_i)`.
- `velMode=0` feeds `sum(s_i)` to both axis curves and the global curve.
- `velMode=1` feeds cardinal X/Y evidence plus `1/sqrt(2)` times the sum of
  diagonal evidence to the respective axis curves; global still receives the
  unweighted sum.
- Every curve uses `offset + beta * s^alpha * exp(gamma * s)`.

`ParametersClass::setDefaults` at `0x100197f0` establishes the curve defaults
as `beta=1`, `alpha=1`, `gamma=0`, `offset=0`. The bundled `prm.cfg` overrides
beta, gamma and offset but not alpha; its effective alpha is therefore exactly
`1`, not an inferred value. `LegacyMotionParameters` and
`LegacyMotionControllerFactory` now load the motion portion of that file and
the existing `.power`/`.lr` calibration files. `--vthresh` maps to field
`0x2c4`, the post-curve per-axis dead zone. Its recovered default is `1e-6`;
the factory uses it unless a modern caller explicitly supplies an override.

`LegacyVelocityMapper` implements this stage. The legacy caller then multiplies
the X/Y curve values by the raw direction axes, applies the configured axis
dead zone and maximum limit, and normalizes the resulting vector. Mapping the
result to cursor pixels and temporal smoothing remain intentionally separate
modern-runtime decisions.

## Next controlled Windows experiments

Run legacy tools only with output outside the supplied distribution. Preserve
command lines, stdout/stderr, output file hashes, and exact WAV input hashes.
This can provide a numerical oracle for the modern frontend without making the
executable a product dependency.

### 2026-09-24: `frontend.exe` Windows smoke-run

Known: after adding the absolute paths to `vjlib/lib/win32` and the bundled
VC90 runtime directory to `PATH`, `frontend.exe --help` loaded successfully and
read `params/prm.cfg`. The executable recognised the options `--maxFrames`,
`--mode`, `--mfccFile`, `--powerFile`, `--zerocrossFile`, and `--pitchFile`.

Known: an attempted one-frame (`--maxFrames 1 --mode 0`) MFCC capture wrote no
output and exited with Windows status `0xC0000005` (`-1073741819`, access
violation) after printing the selected output path. The original legacy tree
was not modified. Therefore this executable is not currently a dependable
numerical oracle on this modern Windows host.

Next: investigate the crash from a Windows debugger only in an isolated copy,
or recover frontend parameters through static analysis and validate the new
implementation using documented invariants until a safe oracle is available.

### 2026-09-24: Windows XP smoke capture

The legacy distribution ran in Windows XP. A live, uncontrolled microphone
capture used `--maxFrames 100 --mode 0` and wrote `mfccFile`, `powerFile`,
`zerocrossFile`, and `pitchFile` to a VMware shared folder. The transferred
source artifact is `input/smoke.zip`; it is preserved as supplied.

Confirmed from the file lengths and decoded values:

- `mfcc.txt` is not text: it is a raw little-endian IEEE-754 `float32` stream.
  Its 41,600 bytes are exactly 10,400 floats, or 400 records of 26 values.
- `pitch.txt` is also a raw little-endian IEEE-754 `float32` stream. Its
  1,600 bytes are exactly 400 values, aligning one-to-one with the MFCC
  records.
- `power.txt` and `zerocross.txt` were created but have zero length in this
  mode/run. This is an observation, not evidence that those measures are
  absent from the legacy pipeline.

This capture cannot establish MFCC parameters because the exact input waveform
was not retained.

### 2026-09-24: Windows XP live-capture duration check

`input/count-10.zip` contains a second uncontrolled microphone capture made
with `--maxFrames 10` and stopped manually with Ctrl+C after the requested
approximately five-second interval. `mfcc.bin` has 29,120 bytes: 7,280
little-endian `float32` values, exactly 280 records of 26 values. `pitch.bin`
has 1,120 bytes: exactly 280 `float32` values. The two streams therefore stay
one-to-one in a separate run.

`frontend.exe` did not terminate automatically at `--maxFrames 10`; live
capture continued until Ctrl+C. Thus `maxFrames` must not be treated as a
capture-duration limit. The different record counts between uncontrolled live
captures cannot establish its internal meaning; a fixed audio input is needed
for that experiment.

### 2026-09-24: Windows XP 440 Hz controlled-audio capture

The first reproducible source WAV is
`artifacts/legacy-reference/wav/sine_440hz_5s.wav`, SHA-256
`56b89c41ffe38b5758a6caa987020b316b6532d1c5118298b9f7888d29713c59`.
It is mono, PCM 16-bit, 44,100 Hz, with one second of silence at each edge and
a five-second, 440 Hz, 25%-amplitude sine between them. Windows XP routed it
through a VB-Audio virtual cable into the legacy `frontend.exe`; the raw legacy
outputs are preserved in `input/wav-440.zip`, SHA-256
`eceb2cd413eb228e4f3cebf20a8511f26eb2bd04a89fd9386ee69da6fc747684`.

Confirmed: `mfcc.bin` contains 400 records of 26 `float32` values and
`pitch.bin` contains 400 corresponding `float32` values. Successive MFCC
records during the tone are highly stable. Pitch starts with values near
444.444 Hz, then changes to approximately 146.789 Hz (a subharmonic result).
This proves that the virtual routing and the legacy feature path are active;
it does not yet prove the legacy pitch algorithm's intended semantics or exact
input-to-output frame alignment.

### 2026-09-24: Windows XP 1000 Hz controlled-audio capture

The second reproducible source is
`artifacts/legacy-reference/wav/sine_1000hz_5s.wav`, SHA-256
`68a39bae09b92538b48e1e54029f96a5f4fddeec0c85c19ee21513477d4eb42d`.
The corresponding legacy output is `input/wav-1000.zip`, SHA-256
`434ec6d7854876a5398caab643aee0476fb054fe0178e335f1438d0fbe78623b`.
It has 400 MFCC records of 26 `float32` values and 400 pitch values, matching
the raw stream layout confirmed by the 440 Hz run. The steady-state MFCC
vector differs materially from the 440 Hz vector, proving frequency-dependent
feature extraction on the controlled input. All pitch values are 500 Hz even
though the input is 1000 Hz; pitch is therefore not a direct input-frequency
oracle at this point.

### 2026-09-24: Windows XP 2000 Hz controlled-audio capture

`artifacts/legacy-reference/wav/sine_2000hz_5s.wav` (SHA-256
`bba060c5b19301579043c896896658d76338e55b31e693c93973ac98ff661156`)
was captured through the same virtual route. Its legacy output is
`input/wav-2000.zip`, SHA-256
`1957a50e8bcc539751481a677cfa3cd66f605f13b2fb4e10ff13ad887a96aecc`.
It again contains 400 MFCC records of 26 `float32` values and 400 pitch
values. Its steady-state MFCC vector differs from both 440 Hz and 1000 Hz.
The pitch output is primarily 500 Hz, with several `-0.1` and 53.333 Hz
values, further confirming that pitch requires separate reverse engineering.

Across the three controlled tones, the first 13 values form the changing
steady-state cepstral vector. The remaining 13 values are near zero for the
stable 1000 Hz and 2000 Hz regions; `vj.norms` likewise has near-zero means
and substantially different scales for values 14--26. This is strong evidence
that a 26-value feature record consists of 13 static MFCC-like coefficients
followed by 13 temporal delta coefficients. The exact delta window/formula is
still unknown, so this is not yet an implementation contract.

### 2026-09-24: Windows XP 440 Hz to 1000 Hz transition

`input/step-440-1000.zip` (SHA-256
`3e620a419731d25cf97c5ac294bd70a93f858e2d86dba6ea1fa22393d5611711`)
contains the legacy result for the controlled
`step_440hz_to_1000hz.wav` source. It has 400 records of 26 `float32`
values. The static spectrum changes around record 250, as expected from the
2.5-second source-tone transition.

This experiment upgrades the delta finding to an interior-frame implementation
contract. For every coefficient `i` in 0..12 and every checked stable record
`t` in 20..229, the legacy value at index `13+i` matches, with maximum absolute
error `1.67e-7`, this formula:

```text
delta[i,t] = ((static[i,t+1] - static[i,t-1])
            + 2 * (static[i,t+2] - static[i,t-2])) / 10
```

This is a centred ±2-frame delta. The legacy start/end boundary policy remains
unknown and must not be guessed.

### Prepared 16 kHz validation assets

The original controlled WAVs above are 44.1 kHz and are useful for behavioural
checks, but they enter a legacy microphone path that the binary configures at
16 kHz. Their feature vectors can therefore include an unobserved virtual-cable
resampling step.

`artifacts/legacy-reference/wav-16khz/` now contains deterministic 16-bit,
mono, 16 kHz variants of the same tones and transitions. They were generated
with:

```text
python3 tools/generate_reference_wavs.py artifacts/legacy-reference/wav-16khz --sample-rate 16000
```

The next XP capture should play one of these files and preserve the resulting
raw MFCC/pitch files. The XP virtual-audio driver may not expose a selectable
endpoint format; that does not invalidate the experiment because the legacy
engine itself requests 16 kHz mono. Until the result is compared, these WAVs
are prepared inputs, not evidence.

### 2026-09-24: Windows XP 16 kHz 440 Hz controlled-audio capture

`input/legacy-16khz-capture.zip` is the capture made from
`input/legacy-16khz/sine_440hz_5s.wav`. Its SHA-256 is
`93e61c8893e2ecc1a2b804a961b890efc893745634b9ee80ed7bf69c0f96655d`.
The archive contains `mfcc.bin` (41,600 bytes, 400 records of 26 `float32`
values) and `pitch.bin` (1,600 bytes, 400 `float32` values); `power.bin` and
`zerocross.bin` are empty as in prior mode-0 captures. The extracted MFCC
stream SHA-256 is
`5052cf8d3a72a1969bf31153b43e80f9b3a253283a7eff83459f548878731af0`.

After startup the static 13-value MFCC portion settles into a five-frame phase
cycle. That is consistent with a 440 Hz sine through a 16 kHz input path and a
160-sample hop: each hop advances the waveform by 4.4 periods, so the
fractional phase repeats every five frames. It is strong framing evidence but
not, by itself, proof that the virtual audio route did no gain adjustment or
resampling.

### 2026-09-24: Direct MFCC filter-table capture

`input/LegacyFilterProbe.exe` is a 32-bit, read-only diagnostic built from
`tools/legacy_filter_probe.c`. In an isolated XP process it invoked the DLL's
normal lazy filter initializer, then printed the table without modifying the
legacy installation. The resulting `input/legacy-filterbank.txt` directly
contains all 23 bin ranges and weights.

This resolves the filterbank exactly: its vertices are
`[2, 5, 8, 11, 14, 18, 23, 27, 33, 38, 45, 52, 60, 69, 79, 89, 101, 115,
129, 145, 163, 183, 205, 229, 256]`; filters have inclusive, non-zero edge
weights rather than textbook zero-weight edges. `LegacyMfccExtractor` now
implements this recovered contract, including the DC-blocker output used as
the final pre-emphasis look-ahead. Direct numerical comparison with the XP
audio trace still needs audio-cable gain/alignment accounting and float-level
rounding comparison.

### 2026-09-24: MFCC numerical cross-check

`tools/compare_legacy_capture.py` replays the 16 kHz 440 Hz source through the
recovered MFCC equations. It searches the 400-sample sine period for capture
phase and derives the virtual-cable gain from each frame's legacy energy. With
the stable XP records 3--7, it found phase 16 samples, gain `0.505652922`, and
an RMS difference of `0.0134481` across all 65 static values. The largest
per-frame absolute differences were `0.01101`, `0.00611`, `0.00378`,
`0.00750`, and `0.08473`.

This is strong independent confirmation of the frame length, recursive DC
blocker, forward pre-emphasis, FFT size, exact filter table, logarithm, and
DCT ordering. It is not byte-for-byte proof: the XP cable may resample or
vary its phase, and the legacy 32-bit float/x87 arithmetic does not match the
modern implementation's `double` FFT intermediates exactly.

## Legacy training workflow and mouse actions

Static IL inspection of the supplied `ControlPanel.exe`, the batch files, and
the native mouse controller establishes two separate enrollment tracks:

- A vowel button records one labeled class (`s_<id>`) through
  `frontend.bat --mode 1`. It asks the frontend to write power, zero-crossing,
  MFCC, and pitch feature files. When the panel's recording checkbox is on,
  it also preserves a timestamped raw WAV (`fe_s_<id>_...wav`).
- A discrete-sound button records a command class (`d_<id>`) through
  `frontend.bat --mode 2`, producing power, zero-crossing, and MFCC files; raw
  WAV preservation is likewise optional. The distributed `vj.dict` currently
  has just two command pronunciations, `k` and `ch`.
- The panel enables adaptation only when all classes required by the chosen
  vowel configuration and the configured discrete commands have non-empty
  MFCC and power recordings. It then calls `adapt.bat` with the user and class
  counts. That pipeline adapts the vowel MLP, RSC and ML-RSC models, then the
  discrete HMM model, and separately combines/adapts power and pitch data.
  The base models are inputs; adapted per-user models are outputs. This is a
  multi-model feature adaptation workflow, not a single raw-WAV centroid
  classifier.

The native `MouseController::discSoundReceived` handler contains these
voice-command effects when the `--doClick` parameter is enabled:

- discrete ID 0 emits a left-button down/up pair (single left click);
- discrete ID 1 toggles left-button down versus up (press-and-hold/release,
  which permits dragging while cursor motion is active);
- discrete ID 2 toggles an internal armed state. This state gates IDs 0/1.

The supplied dictionary has only IDs 0 and 1, while the desktop panel exposes
two discrete recording buttons; therefore the observed ID-2 arm gate cannot
be produced from this supplied configuration. It may be for another model or
configuration, and should not be silently copied into the first modern
workflow without resolving that mismatch. The handler contains no right-click
or double-click voice action. `MouseDevice` has right/middle click helper
methods, but their existence alone does not show that the recognizer can
trigger them. The sample `keymap.txt` maps command IDs to keyboard actions,
not mouse actions, so the mouse behavior is a separate controller path.

For the modern first learning milestone, the evidence supports preserving the
two-track concept: user-labeled continuous classes for motion, plus discrete
command examples for click actions, each with optional raw-WAV retention.
Reusing that enrollment shape is safer than inventing a unified seven-class
model. It does not yet establish that the old adaptation executables or their
model formats should be embedded in the new application; that remains a
separate compatibility/product decision. Initially, only the left-click
behavior is an unambiguous simple click. The hold/release action needs a
visible, fail-safe state and explicit release on pause/stop before it is safe
to expose.
