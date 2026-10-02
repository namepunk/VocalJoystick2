# Vocal Joystick — implementation brief for Codex

## 0. Mission

Build a **modern Windows desktop application** inspired by and technically grounded in the legacy Vocal Joystick implementation contained in this directory.

The goal is **not** to recreate the old UI or blindly port the old source. The goal is to create a high-quality, maintainable, modern Windows application for people with motor impairments that uses the recovered legacy models/algorithms as a starting point and replaces obsolete infrastructure with clean modern implementations.

The application must support:

1. real-time voice-controlled cursor movement;
2. discrete voice commands such as click and drag/toggle;
3. a guided user-training/adaptation workflow;
4. a normal working mode;
5. diagnostics and calibration;
6. a high-quality, accessible Windows UI;
7. a modular architecture so the legacy models can later be replaced or supplemented by modern models.

**Do not modify, overwrite, or delete the legacy files. Treat the supplied archive/extracted directory as read-only reference material.**

---

# 1. What is in this directory

The directory comes from an old Windows distribution of Vocal Joystick.

Important components discovered during initial analysis include:

- `libvj.dll` — native C++ core;
- `VJ.Engine.dll` — C++/CLI/.NET bridge;
- `vj.exe`;
- `frontend.exe`;
- `ControlPanel.exe`;
- `adapt.exe`;
- `RSCAdapt.exe`;
- `MLRSCAdapt.exe`;
- `hmmadapt.exe`;
- `pitchadapt.exe`;
- `poweradapt.exe`;
- `portaudio.dll`;
- `libsndfile-1.dll`;
- Visual C++ 2008 runtime components.

Important data/model directories include:

```text
vjlib/data/
    params/
    bmmf/
    adaptation/
    users/default/
    dict/
    sound/
```

Important model/configuration files include:

```text
vjlib/data/vj.norms
vjlib/data/vj.hmmf
vjlib/data/vj.dict

vjlib/data/users/default/
    s4.wts
    s5.wts
    s8.wts
    s9.wts
    s4.rsc
    s5.rsc
    s8.rsc
    s9.rsc
    s4.mlrsc
    s5.mlrsc
    s8.mlrsc
    s9.mlrsc

vjlib/data/.../good_mono_*.wts
vjlib/data/.../good_mono_*.mlrsc
```

There are also adaptation scripts and training data.

The legacy binaries appear to come from a later revision than the 2005 Vocal Joystick paper. The extracted source/revision information indicates revision 208 with a 2007-01-11 change date, while the Windows binaries were built later. Therefore:

**Do not assume the 2005 paper is the exact specification of this binary. Use the actual files in this directory as the primary technical reference.**

The 2005 paper is useful for understanding the conceptual architecture, but the recovered models/configuration and the actual binary behavior have priority.

---

# 2. Important discoveries from the legacy implementation

## 2.1 Audio

The historical system uses:

- PCM audio;
- 16 kHz processing;
- short-time frames;
- energy/power;
- zero-crossing information;
- pitch/NCCF-related processing;
- MFCC features.

The paper describes:

- energy frame: 25 ms;
- energy step: 10 ms;
- pitch frame: 40 ms;
- pitch step: 10 ms.

The legacy system shares features among tasks.

Implement a modern audio layer that can capture microphone input continuously and resample it to the model's expected rate.

The application must handle:

- microphone selection;
- sample-rate conversion;
- device changes;
- audio dropouts;
- permissions;
- clean start/stop;
- low-latency streaming;
- diagnostics.

---

# 3. Feature extraction

The legacy classifier uses **26-dimensional MFCC features**.

The key discovered architecture is:

```text
26 MFCC coefficients
        ×
7 consecutive frames
        =
182-dimensional input vector
```

This is critical.

Do not implement a classifier that receives only one 26-dimensional frame if the legacy model is being used.

The supplied `vj.norms` contains per-feature normalization information:

- 26 means;
- inverse standard deviations.

The intended normalization is effectively:

```text
normalized[i] = (x[i] - mean[i]) * inverseStd[i]
```

Implement this in a reusable `FeatureNormalizer`.

The feature extractor must be deterministic and testable.

Create unit tests for:

- frame boundaries;
- hop size;
- MFCC dimensionality;
- normalization;
- seven-frame context construction.

Do not silently invent feature-processing parameters if they can be recovered from the supplied binaries/configuration. Search the legacy implementation for exact values first.

---

# 4. Legacy MLP model

The legacy distribution contains actual neural-network weight files.

Examples include:

```text
good_mono_4N_*.wts
good_mono_5N.wts
good_mono_8N.wts
good_mono_9N.wts
```

A discovered `.wts` format contains dimensions such as:

```text
182
50
8
```

and weight blocks corresponding to:

```text
182 × 50
50
50 × 8
8
```

So the 8-class network is:

```text
182
 ↓
50 hidden
 ↓
8 output
```

The discovered native code contains methods with names such as:

```text
mlpNNAdapt::forwardInput2Hidden
mlpNNAdapt::forwardHidden2Output
mlpNNAdapt::forwardInput2Output
mlpNNAdapt::trainSample
mlpNNAdapt::adaptSample
mlpNNAdapt::adaptBuffer
mlpNNAdapt::transInput
```

The initial binary inspection indicates:

- hidden layer uses a logistic sigmoid;
- output uses softmax;
- output values represent class probabilities.

Implement a clean modern `LegacyMlpClassifier` that can load the supplied `.wts` files without depending on the old DLL.

Do not hard-code the weights into source code.

Create a model loader and preserve the original model files separately.

---

# 5. ML-RSC model

The most important configuration discovery is that the shipped configuration appears to use:

```text
classMode = 2
```

with the legacy help identifying:

```text
0 = NN
1 = PSD
2 = ML-RSC
```

Therefore the default legacy classifier is apparently **ML-RSC**, not simply the historical MLP.

The supplied ML-RSC models include:

```text
good_mono_4N.mlrsc
good_mono_5N.mlrsc
good_mono_8N.mlrsc
good_mono_9N.mlrsc
```

An inspected header for an 8-class model contains:

```text
26
182
100
8
```

which corresponds to:

```text
26 features
182-dimensional temporal input
100 hidden/internal units
8 outputs
```

The exact ML-RSC mathematical implementation is not yet completely documented.

**Your first technical task is therefore to reverse-engineer and document the `.mlrsc` format and the corresponding inference functions in `libvj.dll`.**

Do not guess.

Use:

- binary inspection;
- strings/symbols;
- disassembly/decompilation;
- model files;
- the legacy executables;
- configuration;
- runtime experiments where practical.

Then implement a standalone `LegacyMlRscClassifier`.

---

# 6. Vowel/direction classes

The historical system supports several class counts:

- 4;
- 5;
- 8;
- 9.

The paper describes the four corner vowels as directional controls:

- up;
- down;
- left;
- right.

The actual legacy installation contains 4/5/8/9-class models.

Do not assume that 8 classes simply means eight independent cursor directions.

Determine from the legacy configuration/model metadata which classes correspond to which vocalizations and how their probability vectors are converted into movement.

Create a configuration-driven class map rather than hard-coding class meanings.

---

# 7. Continuous motion control

The historical architecture does not simply do:

```text
argmax(class)
```

and move the cursor.

The paper describes continuous direction based on the weighted vowel probabilities and continuous speed based on energy.

The legacy DLL also exposes concepts/functions including:

```text
MotionExtractor::patternDataReceived
MotionData::xDir
MotionData::yDir
MotionData::velocity
MotionData::globalVel
MotionData::vowelProbs
MotionData::energy
MotionData::relPitch
MotionData::getMovementValues
```

Therefore implement a dedicated:

```text
MotionController
```

with separate stages:

```text
class probabilities
        ↓
direction vector
        ↓
confidence/dead zone
        ↓
temporal smoothing
        ↓
velocity
        ↓
acceleration/deceleration
        ↓
cursor movement
```

The system must support:

- dead zone;
- configurable sensitivity;
- acceleration;
- maximum speed;
- minimum speed;
- smoothing;
- direction-change responsiveness;
- low-confidence suppression;
- pause/voice inactivity;
- optional adaptive behavior.

Do not assume the exact old formula is optimal for a modern application. Recover the old formula first, then make the modern controller configurable.

---

# 8. Energy and speed

The historical system uses energy as a continuous control parameter for cursor speed.

The paper describes relative energy normalized per vowel because different vowels have different intrinsic loudness.

It describes a velocity formulation based on normalized energy and parameters such as:

```text
V = beta * s^alpha * exp(gamma * s)
```

with historical parameters including beta/gamma/alpha.

The modern application should expose a user-friendly sensitivity/velocity curve rather than forcing users to understand these mathematical parameters.

However, retain the low-level parameters in an advanced diagnostics/configuration layer.

---

# 9. User adaptation / training mode

The application MUST have a first-run guided training workflow.

The legacy system has explicit adaptation tooling and user-specific models:

```text
adapt.exe
RSCAdapt.exe
MLRSCAdapt.exe
hmmadapt.exe
pitchadapt.exe
poweradapt.exe
```

and:

```text
users/default/
    s4.*
    s5.*
    s8.*
    s9.*
```

The historical workflow records several seconds of each target vocalization. The paper describes approximately 2–3 seconds per predefined vowel.

The modern training UI should:

1. explain the task in plain language;
2. show which sound/vocalization to make;
3. provide live microphone level;
4. show voice activity;
5. record the required duration;
6. reject unusable samples;
7. allow retry;
8. show progress;
9. build/store a user profile;
10. allow re-training a single class;
11. allow resetting to the base model;
12. never destroy the original base models.

The training process should be modular so that:

```text
base model
    +
user samples
    ↓
adapted user model
```

can be regenerated.

Initially, implement the historical adaptation algorithms where they can be recovered confidently.

If exact adaptation is not yet understood, implement the infrastructure and a safe baseline, clearly marking the algorithm as provisional. Do not invent undocumented behavior and present it as legacy-compatible.

---

# 10. Discrete voice commands / click

This is a separate recognition problem.

Do NOT implement click as a vowel classifier threshold.

The historical architecture uses HMM/GMM-based recognition.

The supplied files include:

```text
vj.hmmf
vj.dict
```

The dictionary contains:

```text
0 k
1 ch
```

corresponding to the historical discrete vocalizations.

The paper describes:

```text
consonant / CV / CVC
        ↓
phone HMM
        ↓
word HMM
        ↓
discrete command
```

HMMs are used because the duration of a spoken command varies.

The DLL contains functions/classes including:

```text
BMM::viterbiDecode
BMM::vitInference
BMM::fullInference

EBMM::viterbiDecode
EBMM::vitInference
EBMM::fullInference

GaussianMixture::cmpteObsPrbs
GaussianMixture::cmpteObsPrbsAndCache

EBMM::forcedAlignment
EBMM::shortestPath
```

Implement a standalone modern HMM/GMM engine capable of loading/reproducing the supplied model.

The click pipeline should include:

```text
audio
 ↓
voice activity / temporal segmentation
 ↓
MFCC
 ↓
GMM/HMM likelihoods
 ↓
Viterbi decoding
 ↓
confidence/rejection
 ↓
command event
```

Support at minimum:

- left click;
- drag/toggle;
- configurable command sounds;
- command cooldown;
- false-positive rejection;
- configurable confidence threshold;
- configurable minimum/maximum duration;
- silence termination.

The historical configuration contains parameters related to:

- zero-crossing thresholds;
- pitch/voicing thresholds;
- minimum voiced/unvoiced lengths;
- discrete-sound lengths;
- accepted vowel/discrete probabilities;
- key delay.

Recover their actual semantics before implementing them.

---

# 11. Voice activity and temporal processing

The legacy configuration and binary indicate significant temporal logic around voiced/unvoiced states.

Relevant concepts include:

```text
minzcross
pthresh
unvBias
unvSlope
unvLen
vstart
keyDelay
```

and additional parameters for:

- minimum zero crossings;
- vowel validity;
- silence frames needed to terminate voiced state;
- initial unvoiced frames;
- minimum/maximum discrete-sound duration;
- minimum accepted probabilities.

Implement a reusable `VoiceActivityDetector` / `SpeechSegmenter`.

Do not tie it directly to click recognition.

---

# 12. Pitch

Pitch is present in the legacy system and there are:

```text
pitchadapt.exe
```

and DLL methods/structures associated with pitch/NCCF.

The historical paper says pitch was extracted but was not used as a primary control parameter in the WIMP prototype.

For the modern application:

- implement pitch extraction if practical;
- expose it in diagnostics;
- keep it available to future control models;
- do not make it mandatory for basic cursor control unless experiments demonstrate value.

---

# 13. Filtering / Kalman

The native DLL contains:

```text
PatternExtractor::doKalman
PatternExtractor::filterCoeff
```

This suggests temporal filtering beyond raw classifier output.

Investigate this code and recover the behavior.

The modern application should have a dedicated temporal-filtering layer, potentially supporting:

- exponential smoothing;
- median filtering;
- Kalman filtering;
- configurable response time.

The default should favor low latency and stable control rather than maximum smoothing.

---

# 14. Frontend/reference utility

The legacy distribution contains `frontend.exe`.

Its arguments indicate it can output:

```text
MFCC
power
zero crossing
pitch
```

and has modes for:

```text
speech
vowel
discrete
```

Use this executable as a forensic/reference tool when necessary.

Do not make the modern application depend on it.

The modern application must implement its own audio frontend.

---

# 15. Modern Windows application requirements

Build a polished Windows desktop application.

Preferred stack:

- .NET 8 or newer supported LTS;
- C#;
- WinUI 3 preferred for the UI, unless there is a strong technical reason to use WPF;
- native Windows input APIs where required;
- no dependency on the legacy VC90 runtime at runtime.

The application should be installable and usable by a non-technical person.

## Main screens

### Dashboard / Home

Show:

- microphone status;
- current profile;
- current mode;
- recognition status;
- quick start/stop;
- calibration/training status;
- current sensitivity;
- help/accessibility shortcuts.

### Training

Guided wizard:

```text
Introduction
→ microphone setup
→ voice level test
→ vocalization training
→ click-command training
→ validation
→ save profile
```

### Work / Control

Show:

- active/inactive;
- current recognized direction;
- confidence;
- speed;
- voice activity;
- discrete command events;
- optional compact overlay.

The UI should not interfere with cursor control.

### Settings

Categories:

- microphone;
- audio;
- movement;
- sensitivity;
- speed;
- smoothing;
- click commands;
- accessibility;
- profile management;
- diagnostics;
- advanced.

### Diagnostics

Provide a developer-oriented view showing:

- waveform;
- energy;
- pitch;
- MFCC status;
- current classifier;
- class probabilities;
- direction vector;
- velocity;
- VAD state;
- HMM command confidence;
- processing latency;
- dropped audio frames.

Allow logging to a file.

---

# 16. Accessibility requirements

This is an accessibility application. Treat accessibility as a first-class requirement.

Support:

- keyboard navigation;
- large text;
- high contrast;
- scalable UI;
- clear focus indicators;
- screen-reader-friendly labels where applicable;
- minimal number of required interactions;
- large controls;
- explicit state indicators;
- safe emergency stop;
- ability to pause voice control immediately;
- configurable startup behavior.

The user must always have an obvious way to disable voice cursor control.

---

# 17. Profiles

Implement user profiles.

Each profile should contain:

```text
profile/
    settings.json
    calibration.json
    adapted models
    training metadata
```

Never modify the supplied legacy model directory.

Base models should be immutable.

A user profile may override:

- class model;
- normalization;
- energy calibration;
- pitch calibration;
- thresholds;
- movement parameters;
- click model.

Profiles must be exportable/importable.

---

# 18. Safety and usability

Voice cursor control can cause unintended actions.

Implement:

- global pause hotkey;
- visible active-state indicator;
- startup disabled by default until explicitly enabled;
- configurable click arming;
- command cooldown;
- confidence threshold;
- dead zone;
- optional "hold to activate" mode;
- emergency stop;
- safe handling of microphone disconnects.

Never issue mouse clicks when the application is not confident enough.

---

# 19. Architecture

Use clean separation of concerns.

Suggested solution:

```text
VocalJoystick.sln

src/
    VocalJoystick.App
    VocalJoystick.Core
    VocalJoystick.Audio
    VocalJoystick.Features
    VocalJoystick.Models
    VocalJoystick.Motion
    VocalJoystick.Commands
    VocalJoystick.Profiles
    VocalJoystick.Infrastructure

tests/
    VocalJoystick.Core.Tests
    VocalJoystick.Features.Tests
    VocalJoystick.Models.Tests
    VocalJoystick.Motion.Tests
    VocalJoystick.Commands.Tests

tools/
    ModelInspector
    LegacyModelConverter

docs/
    architecture.md
    legacy-analysis.md
    model-formats.md
```

Core components:

```text
IAudioSource
IAudioFrontend
IFeatureExtractor
IFeatureNormalizer
IClassifier
IMotionController
ICommandRecognizer
IVoiceActivityDetector
IUserProfile
IModelLoader
```

Keep model inference independent from Windows UI.

---

# 20. Performance requirements

This is a real-time application.

Design for:

- low latency;
- no UI thread blocking;
- bounded allocations;
- stable real-time processing;
- audio callback safety;
- asynchronous diagnostics/logging;
- graceful recovery from audio errors.

Target end-to-end perceived control latency well below 100 ms where practical.

The historical paper reports approximately 60 ms change latency, so this is a useful engineering reference, not an absolute requirement.

---

# 21. Model format work

Before implementing the application completely, create a `ModelInspector` tool.

It should inspect:

```text
.wts
.mlrsc
.rsc
.hmmf
.norms
.dict
```

and output human-readable information:

- dimensions;
- layer sizes;
- number of weights;
- number of states;
- number of mixtures;
- feature dimensionality;
- class count;
- metadata;
- checksum/hash;
- file version if detectable.

Do not destroy or rewrite original files.

If conversion is implemented, write converted models into a separate generated directory.

---

# 22. Reverse-engineering workflow

Use the supplied legacy binaries as technical evidence.

For unknown behavior:

1. inspect configuration;
2. inspect model file structure;
3. inspect strings/symbols;
4. inspect native functions;
5. disassemble/decompile;
6. correlate with model dimensions;
7. implement;
8. write a small deterministic test;
9. document what is known vs inferred.

Do not make unsupported assumptions.

For each recovered algorithm, maintain:

```text
Known:
    directly observed in binary/model/configuration

Likely:
    strongly supported inference

Unknown:
    not yet established
```

---

# 23. What NOT to do

Do not:

- simply wrap `libvj.dll` and call it the finished application;
- require the user to install Visual C++ 2008;
- make the old EXE the runtime dependency;
- hard-code neural-network weights into C# source;
- replace HMM click recognition with a simple amplitude threshold;
- throw away the supplied models and immediately train a completely unrelated neural network;
- assume the 2005 paper exactly matches the 2008 Windows build;
- change the legacy files;
- claim an algorithm has been reproduced when it has only been guessed;
- spend weeks rebuilding the old UI.

The legacy code/data are the **research/reference layer**.

The final application must be a clean modern implementation.

---

# 24. Development phases

## Phase 1 — repository and forensic inventory

Create:

```text
docs/legacy-analysis.md
```

Document:

- binaries;
- configs;
- model files;
- dimensions;
- class counts;
- discovered algorithms;
- unknowns.

Create `ModelInspector`.

Do not build the full UI yet.

## Phase 2 — audio/features

Implement:

- microphone capture;
- resampling;
- framing;
- energy;
- zero crossing;
- MFCC;
- normalization;
- 7-frame context.

Add unit tests.

## Phase 3 — legacy inference

Implement:

- `.wts` loader;
- MLP inference;
- `.mlrsc` loader;
- ML-RSC inference after reverse engineering;
- model selection.

## Phase 4 — continuous control

Implement:

- probability → direction;
- energy → velocity;
- filtering;
- acceleration;
- dead zone;
- mouse output.

## Phase 5 — discrete commands

Implement:

- HMM/GMM model loading;
- Viterbi;
- segmentation;
- confidence/rejection;
- click/drag commands.

## Phase 6 — adaptation

Implement:

- training wizard;
- user samples;
- calibration;
- adapted models;
- profile storage.

## Phase 7 — modern UI

Implement the complete Windows application.

## Phase 8 — hardening

Test:

- microphone changes;
- sleep/wake;
- application crashes;
- high CPU load;
- audio dropouts;
- false clicks;
- accidental activation;
- profile switching;
- installer/uninstaller.

---

# 25. Definition of Done

The project is not done when it compiles.

A first complete release should allow a user to:

1. install the application;
2. select a microphone;
3. run the microphone test;
4. create a user profile;
5. complete guided vocal training;
6. start voice control;
7. move the cursor continuously;
8. stop/pause control safely;
9. perform a click using the trained discrete sound;
10. perform drag/toggle;
11. adjust sensitivity;
12. view confidence and diagnostics;
13. save/export the profile;
14. restart the application and retain the profile.

The code should be maintainable and documented.

---

# 26. Immediate first task for Codex

Do NOT start by implementing the entire application.

First:

### A. Inventory this supplied directory

Generate:

```text
docs/legacy-inventory.md
```

### B. Analyze

At minimum:

```text
libvj.dll
VJ.Engine.dll
frontend.exe
adapt.exe
RSCAdapt.exe
MLRSCAdapt.exe
hmmadapt.exe
vj.norms
vj.hmmf
vj.dict
*.wts
*.rsc
*.mlrsc
*.cfg
adapt.bat
```

### C. Reverse engineer

Determine:

1. exact MFCC pipeline;
2. exact 7-frame context ordering;
3. `.wts` format;
4. `.mlrsc` format;
5. ML-RSC inference algorithm;
6. `.rsc` format;
7. HMM/GMM file format;
8. Viterbi implementation;
9. command segmentation;
10. direction-vector calculation;
11. velocity calculation;
12. filtering/Kalman behavior;
13. user adaptation behavior.

### D. Then produce

```text
docs/legacy-analysis.md
docs/model-formats.md
```

with evidence and confidence levels.

### E. Only after that

Start implementing the modern application.

---

# 27. Guiding principle

The most important rule for this project:

**Use the legacy implementation to recover the proven signal-processing and recognition knowledge, but do not preserve its obsolete software architecture.**

The final product should feel like a modern Windows accessibility application, not a 2008 reverse-engineered program.

Use the supplied models wherever they are technically sound. Where the old implementation is obsolete or limiting, build a better modern layer around it.

Prioritize:

1. reliable control;
2. very low accidental-click rate;
3. low latency;
4. easy training;
5. accessibility;
6. maintainability;
7. extensibility.

Start with forensic analysis and a small executable/model-inspection tool, then proceed incrementally. Do not make a giant speculative implementation in one step.
