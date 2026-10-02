# Legacy model formats

This document distinguishes direct observations from incomplete reconstruction.
No undocumented payload layout below is an implementation contract.

## `.wts` — Known

The files are ASCII text. Their first three values are respectively input,
hidden, and output cardinalities. All supplied models use 182 inputs and 50
hidden units; output size is 4, 5, 8, or 9. Four labelled blocks follow:

```text
# weigvec
input * hidden
# weigvec
hidden * output
# biasvec
hidden
# biasvec
output
```

For the 4-class model those block counts are `9100`, `200`, `50`, and `4`.
This establishes the file layout. Static disassembly of `mlpNNAdapt` confirms
the inference contract used by the DLL:

1. Treat each first-block row as one hidden neuron, with `input` values per
   row; add its corresponding hidden bias.
2. Apply `1 / (1 + exp(x))` to each hidden value. This is intentionally the
   decreasing form; it is not the more common `1 / (1 + exp(-x))`.
3. Treat each second-block row as one output class, with `hidden` values per
   row; add its corresponding output bias.
4. Apply `exp(score) / sum(exp(all scores))`. The legacy helper does not
   subtract the maximum score before exponentiation.

The modern implementation follows this contract. Byte-for-byte floating-point
equivalence remains outside scope because the VC9 x87 code uses extended
intermediates, whereas the modern code uses `float` operations.

## `vj.norms` — Known

This is ASCII text containing a cardinality of 26, then 26 means, then a second
cardinality of 26 and inverse standard deviations. The file comments call the
second vector `variances (inv std dev)`. Use:

```text
normalized[i] = (feature[i] - mean[i]) * inverseStd[i]
```

## `.rsc` — Known

Files begin with three little-endian unsigned 32-bit integers: feature count
`26`, context-input count `182`, and class count. Static disassembly of
`RSC_base::init` establishes two float32 blocks, in this order:

```text
classCount * contextInputCount * contextInputCount  class matrices B
classCount * contextInputCount                      per-class shifts d
```

For each class, the DLL subtracts its `d` vector from the input, multiplies
the result by the transpose of its row-major `B`, takes the squared norm, then
divides each score by the sum of all class scores and chooses the greatest.
`LegacyRscModel` implements this contract.

## `.mlrsc` — Known / Partially reconstructed

All supplied files begin with four little-endian unsigned 32-bit integers:
feature count `26`, context-input count `182`, hidden count, and class count.
Hidden count is 150 for the 4/5-class models and 100 for the 8/9-class models.
Static disassembly of `MLRSC_base::init` establishes the exact following
float32 blocks, in this order:

```text
classCount * hiddenCount * hiddenCount  class matrices B
classCount * hiddenCount                per-class shifts d
hiddenCount * contextInputCount         input-to-hidden weights
hiddenCount                             hidden biases
```

The recovered count matches every supplied ML-RSC model. Classification first
computes the input-to-hidden affine transform, adds hidden biases, and applies
the decreasing sigmoid `1 / (1 + exp(x))`, just as the legacy MLP does. For
each class it subtracts that class's `d` from the hidden vector, multiplies by
the transpose of its row-major `B` matrix, and uses the sum of squared result
values as the score. The DLL divides every class score by the sum of all class
scores and selects the greatest score. `LegacyMlrscModel` implements this
recovered contract. As elsewhere, exact x87 rounding is outside scope.

## `vj.hmmf` — Known

The file is an ASCII Gaussian-mixture collection. It declares 26 features and
126 mixture models. Each mixture declares a component count; a component has a
mixing coefficient, 26 means, covariance type `0`, and 26 diagonal variances.

Static recovery of `GaussianMixture::cmpteObsPrbs` and its component helper
establishes the acoustic scoring operation:

```text
log N(x; mean, diagonalVariance)
  = -0.5 * (featureCount * log(2*pi) + sum(log(variance)))
    - 0.5 * sum((mean[i] - x[i])^2 / variance[i])

log mixture probability = log-sum-exp(log(mixingCoefficient) + componentLogProbability)
```

The legacy helper clamps a component likelihood to `-1e10`. The managed
`VocalJoystick.Commands` parser and scorer implement this recovered acoustic
stage. After the mixture collection, the same file stores 42 BMM models. Each
has a five-state left-to-right topology in the supplied profile: a
non-emitting start and end state, an upper-triangular transition matrix, and
three emitting mixture IDs. The dictionary paths are `[0, 36, 0]` for `k`
(the distinctive middle BMM uses mixtures 108–110) and `[0, 40, 0]` for `ch`
(middle BMM mixtures 120–122). BMM 0 is shared by both paths.

`LegacyEbmmViterbiDecoder` expands EBMM graph edges and BMM start/end states
into one Viterbi search. It returns only a score and diagnostic state path;
`LegacyCommandScorer` reproduces the legacy read-only comparison: it takes
`exp(viterbiLogScore)`, normalizes that value across all dictionary entries,
and can compare the best relative probability to a supplied `pthresh` value.
It has no command-event output. `LegacyBmmViterbiDecoder` implements the numerical
left-to-right BMM recurrence for an already segmented feature sequence. It is
not yet an EBMM word decoder and has no command or input side effect.

## `vj.dict` — Known

The text pronunciation graph has `EBMM_MPF` magic and two entries. Entry 0 is
orthography `k` with transcription `k`; entry 1 is `ch` with transcription
`C`. Each has five BMM states including non-emitting start (`-1`) and end
(`-2`) states, with a linear sequence of emitting BMM identifiers.

## Evidence queue

1. Use the supplied `frontend.exe` on Windows to produce reference MFCC output
   for a known WAV file and recover exact framing, preprocessing, and feature
   ordering.
2. Validate classifier outputs from real RSC and ML-RSC calls on the XP oracle,
   including the legacy floating-point boundary behaviour.
3. Exercise BMM/EBMM inference with known feature sequences to fix likelihood,
   transition, and Viterbi boundary semantics.
