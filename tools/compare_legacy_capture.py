#!/usr/bin/env python3
"""Compare the recovered MFCC path with an XP binary MFCC capture.

The XP virtual audio cable has an unknown gain and capture start offset.  For
a periodic source this utility tries all phases, derives gain from the legacy
energy coefficient, and reports the closest five-frame sequence.  It is a
diagnostic; it neither changes the source WAV nor the oracle capture.
"""

from __future__ import annotations

import argparse
import cmath
import math
import struct
import wave
import zipfile
from pathlib import Path


FRAME_LENGTH = 400
FFT_LENGTH = 512
FILTER_VERTICES = [
    2, 5, 8, 11, 14, 18, 23, 27, 33, 38, 45, 52, 60, 69, 79, 89, 101,
    115, 129, 145, 163, 183, 205, 229, 256,
]


def fft(values: list[complex]) -> list[complex]:
    values = values[:]
    reversed_index = 0
    for index in range(1, len(values)):
        bit = len(values) >> 1
        while reversed_index & bit:
            reversed_index ^= bit
            bit >>= 1
        reversed_index ^= bit
        if index < reversed_index:
            values[index], values[reversed_index] = values[reversed_index], values[index]

    length = 2
    while length <= len(values):
        rotation = cmath.exp(-2j * math.pi / length)
        for offset in range(0, len(values), length):
            factor = 1 + 0j
            for index in range(length // 2):
                even = values[offset + index]
                odd = values[offset + index + length // 2] * factor
                values[offset + index] = even + odd
                values[offset + index + length // 2] = even - odd
                factor *= rotation
        length *= 2
    return values


def log_with_floor(value: float) -> float:
    return math.log(value) if value > math.exp(-50) else -50.0


def extract(samples: list[float]) -> list[float]:
    if len(samples) != FRAME_LENGTH:
        raise ValueError("Expected exactly 400 samples")
    dc = [0.0] * (FRAME_LENGTH + 1)
    dc[1] = samples[0]
    for index in range(2, len(dc)):
        dc[index] = 0.999 * dc[index - 1] + samples[index - 1] - samples[index - 2]

    energy = sum(value * value for value in dc[:-1])
    fft_input = [0j] * FFT_LENGTH
    for index in range(FRAME_LENGTH):
        hamming = 0.54 - 0.46 * math.cos(2 * math.pi * index / (FRAME_LENGTH - 1))
        fft_input[index] = (dc[index + 1] - 0.97 * dc[index]) * hamming
    spectrum = [abs(value) for value in fft(fft_input[:])[: FFT_LENGTH // 2 + 1]]

    bands: list[float] = []
    for start, peak, end in zip(FILTER_VERTICES, FILTER_VERTICES[1:], FILTER_VERTICES[2:]):
        weighted = 0.0
        for bin_index in range(start, peak + 1):
            weighted += spectrum[bin_index] * (bin_index - start + 1) / (peak - start + 1)
        for bin_index in range(peak + 1, end + 1):
            weighted += spectrum[bin_index] * (end - bin_index + 1) / (end - peak + 1)
        bands.append(log_with_floor(weighted))
    result = [
        sum(band * math.cos(math.pi * coefficient * (index + 0.5) / len(bands))
            for index, band in enumerate(bands))
        for coefficient in range(1, 13)
    ]
    return result + [log_with_floor(energy)]


def load_capture(path: Path) -> list[list[float]]:
    with zipfile.ZipFile(path) as archive:
        member = next(name for name in archive.namelist() if name.endswith("/mfcc.bin"))
        raw = archive.read(member)
    return [list(struct.unpack_from("<26f", raw, offset)) for offset in range(0, len(raw), 26 * 4)]


def load_wav(path: Path) -> list[float]:
    with wave.open(str(path), "rb") as source:
        if (source.getnchannels(), source.getsampwidth(), source.getframerate()) != (1, 2, 16000):
            raise ValueError("Source WAV must be mono 16-bit 16 kHz PCM")
        raw = source.readframes(source.getnframes())
    return list(struct.unpack(f"<{len(raw) // 2}h", raw))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("capture", type=Path)
    parser.add_argument("wav", type=Path)
    parser.add_argument("--first-frame", type=int, default=3,
                        help="first stable oracle frame (default: 3)")
    args = parser.parse_args()
    oracle = load_capture(args.capture)
    wav_samples = load_wav(args.wav)
    target = [row[:13] for row in oracle[args.first_frame:args.first_frame + 5]]
    if len(target) != 5:
        raise ValueError("Capture does not have five requested frames")

    # The sine begins after one second of intentional silence.  Testing a
    # 400-sample period also makes the result independent of capture latency.
    tone = wav_samples[16000:16400]
    best: tuple[float, int, float, list[list[float]]] | None = None
    for phase in range(400):
        unscaled = [extract([tone[(phase + frame * 160 + sample) % 400] for sample in range(400)])
                    for frame in range(5)]
        gain = math.exp(sum(target[index][12] - unscaled[index][12] for index in range(5)) / 10)
        candidate = [extract([gain * tone[(phase + frame * 160 + sample) % 400]
                              for sample in range(400)]) for frame in range(5)]
        rms = math.sqrt(sum((candidate[frame][feature] - target[frame][feature]) ** 2
                            for frame in range(5) for feature in range(13)) / 65)
        item = (rms, phase, gain, candidate)
        if best is None or item[0] < best[0]:
            best = item

    assert best is not None
    rms, phase, gain, candidate = best
    print(f"best phase={phase} samples; cable gain={gain:.9f}; RMS over 5x13={rms:.9g}")
    for index, (actual, expected) in enumerate(zip(candidate, target)):
        maximum = max(abs(left - right) for left, right in zip(actual, expected))
        print(f"frame {args.first_frame + index}: max_abs_error={maximum:.9g}; "
              f"energy expected={expected[12]:.7f} calculated={actual[12]:.7f}")


if __name__ == "__main__":
    main()
