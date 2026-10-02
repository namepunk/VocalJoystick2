# Legacy binary reverse-engineering log

## Scope and method

This log records read-only static analysis of the supplied binaries, using PE
headers, exported C++ symbols, imports, strings, and i386 disassembly. No
legacy file has been modified or executed from this workspace.

Analysed artifacts:

- `vjlib/lib/win32/libvj.dll` — SHA-256
  `a10f108de602023eb044e41ac557ae7d71dec03baed9b04c257cc1f45d4dd975`
- `vjapp/bin/win/frontend.exe` — SHA-256
  `7ee4fd7a8c7ede8d133346e97449f4987afb3c697064ecebadbaee40ead50d25`

`libvj.dll` is a 32-bit PE DLL built by VC9 in November 2008. It is not
obfuscated and exposes decorated C++ symbols, making targeted static analysis
practical.

## Confirmed binary facts

### Audio and frame path

- The DLL exports `FrameExtractor`, `FeatureExtractor`, `FrameData`, and
  `MFCCFrontEnd` symbols. It imports PortAudio, `libsndfile-1`, and `fwrite`.
- `FrameExtractor::OnAudioDataReceived` rejects audio that is not mono; a
  nearby diagnostic string says it supports only 16-bit mono audio.
- Its frame loop waits for `0x3c0` (= 960) sample values, passes 960 values to
  `FrameData::loadWave`, then advances the circular-buffer read position by
  `0xa0` (= 160) sample values. Thus this binary uses 960-sample overlapping
  analysis frames with a 160-sample hop at this stage of the pipeline.
- `VJEngine::Reset` configures microphone input through
  `AudioDataReader::SetInputToMic(16000, 1, deviceId)`. Therefore the active
  microphone path used by `frontend.exe` is 16 kHz mono: the 960-sample frame
  is 60 ms, its 160-sample hop is 10 ms, and the 400-sample MFCC subframe is
  25 ms. File input follows a separate `SetInputToFile` path, so this fact is
  specifically about the microphone/cable capture oracle.
- `FeatureExtractor::audioFrameReceived` invokes DC removal and then the
  MFCC implementation with the literal arguments 400 samples and 13 output
  coefficients. `FrameData::MFCCFrontEnd` performs the same DC-removal then
  MFCC-call pattern. This independently confirms the 13 static coefficients
  observed in the XP streams.
- The MFCC routine calculates the 13th static value as the natural logarithm
  of the sum of squared samples after the recursive DC blocker. Its 401
  outputs from a 400-sample input are `y[0] = 0`, `y[1] = x[0]`, and
  `y[n] = 0.999*y[n-1] + x[n-1] - x[n-2]` through `y[400]`. Values at or
  below an internal threshold are written as `-50.0` instead. It calculates
  this energy before the following spectral preprocessing.
- The spectral preprocessing uses a pre-emphasis coefficient of `0.97`, a
  precomputed 400-point Hamming window, then zero-pads to 512 values before
  a 512-point FFT. The pre-emphasis loop is explicitly forward-looking:
  each of its 400 values is `y[n+1] - 0.97*y[n]`; the extra DC-blocker
  output therefore supplies the final value without an external look-ahead
  sample.
- The FFT stage converts 256 interior complex bins to magnitudes
  `sqrt(real² + imaginary²)` and takes the absolute value at both endpoints.
  It applies 23 triangular, weighted bands, takes a natural logarithm of each
  band value with the same `-50.0` floor, and calculates 12 DCT values.
  Coefficient `k` is `sum(m=0..22, logBand[m] * cos(pi*k*(m+0.5)/23))` for
  `k = 1..12`; there is no DCT-0 output or visible DCT normalization.
- A global initializer constructs exactly 23 filter records (inclusive
  start/end FFT-bin indices and a separately allocated weight vector). Its
  arguments include literal float values `64.0` and `16000.0`.
- The read-only XP probe `input/LegacyFilterProbe.exe` captured the initialized
  table directly. Its 25 filter vertices are exactly
  `2, 5, 8, 11, 14, 18, 23, 27, 33, 38, 45, 52, 60, 69, 79, 89, 101, 115,
  129, 145, 163, 183, 205, 229, 256`. This is the 64 Hz--8 kHz mel-bank at
  16 kHz with 512 FFT bins and the legacy nearest-bin rule.
- Each filter spans three adjacent vertices. Its rising side, including its
  first bin, is `(bin - start + 1) / (peak - start + 1)`; its falling side
  starts after the peak and is `(end - bin + 1) / (end - peak + 1)`. Thus the
  outermost bins have non-zero weights; for example filter 0 is
  `[0.25, 0.5, 0.75, 1, 0.75, 0.5, 0.25]`. This is now a direct implementation
  contract, not an approximation.
- Exported symbols and binary diagnostics name `applyHamming`,
  `autoCorrelation`, `centerClip`, `computePower`, `computeZeroCross`, and
  pitch dynamic-programming/backtracking functions.

### Feature layout

The XP transition oracle proves the full interior-frame formula:

```text
feature[0..12]   = static MFCC-like coefficients
feature[13..25]  = centred delta(feature[0..12], radius 2)
```

The binary's literal 13-output static-MFCC call is consistent with this
oracle-derived layout.

### Runtime behaviour relevant to capture

The binary imports `fwrite`; XP experiments show that short captures can be
processed and displayed by the console yet leave the requested files empty if
the process is interrupted with Ctrl+C before its output buffering flushes.
This is a capture-harness limitation, not evidence of absent feature
processing.

## Not yet proven

- the relation among the 960-sample frame, the 400-sample MFCC subframe, and
  the pitch window;
- start/end handling for the centred delta formula;
- ML-RSC payload/inference and the motion-state machine.

## Next static-analysis targets

1. Decompile the private callee used by `MFCCFrontEnd` to recover filterbank,
   logarithm, and DCT operations.
2. Trace the `fwrite` call sites and CLI option parser to obtain a graceful,
   deterministic dump method for short XP experiments.
