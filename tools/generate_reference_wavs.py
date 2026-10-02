#!/usr/bin/env python3
"""Create deterministic WAV inputs for the Windows XP legacy oracle.

The files deliberately contain one second of silence before and after the
tone.  That gives a live-capture run room to start and stop without clipping
the signal.  The generator uses only Python's standard library so its PCM
format is explicit and reproducible.
"""

from __future__ import annotations

import argparse
import math
import wave
from pathlib import Path


DEFAULT_SAMPLE_RATE = 44_100
AMPLITUDE = 0.25
TONE_SECONDS = 5
EDGE_SILENCE_SECONDS = 1


def pcm16(value: float) -> bytes:
    bounded = max(-1.0, min(1.0, value))
    integer = round(bounded * 32767)
    return integer.to_bytes(2, byteorder="little", signed=True)


def write_sine(destination: Path, frequency_hz: float, sample_rate: int) -> None:
    edge_samples = sample_rate * EDGE_SILENCE_SECONDS
    tone_samples = sample_rate * TONE_SECONDS
    samples = bytearray(edge_samples * 2)
    for index in range(tone_samples):
        value = AMPLITUDE * math.sin(2.0 * math.pi * frequency_hz * index / sample_rate)
        samples.extend(pcm16(value))
    samples.extend(bytes(edge_samples * 2))

    with wave.open(str(destination), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(sample_rate)
        output.writeframes(samples)


def write_tone_burst(
    destination: Path,
    frequency_hz: float,
    burst_seconds: float = 0.1,
    leading_silence_seconds: float = EDGE_SILENCE_SECONDS,
    trailing_silence_seconds: float = 2,
    sample_rate: int = DEFAULT_SAMPLE_RATE,
) -> None:
    """Write one short tone burst between silence for timing/window experiments."""
    leading_samples = round(sample_rate * leading_silence_seconds)
    burst_samples = round(sample_rate * burst_seconds)
    trailing_samples = round(sample_rate * trailing_silence_seconds)
    samples = bytearray(leading_samples * 2)
    for index in range(burst_samples):
        value = AMPLITUDE * math.sin(2.0 * math.pi * frequency_hz * index / sample_rate)
        samples.extend(pcm16(value))
    samples.extend(bytes(trailing_samples * 2))

    with wave.open(str(destination), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(sample_rate)
        output.writeframes(samples)


def write_tone_step(destination: Path, sample_rate: int) -> None:
    """Write a long, flush-safe frequency transition for delta-feature analysis."""
    edge_samples = sample_rate * EDGE_SILENCE_SECONDS
    segment_samples = round(sample_rate * 2.5)
    samples = bytearray(edge_samples * 2)
    for frequency_hz in (440, 1000):
        for index in range(segment_samples):
            value = AMPLITUDE * math.sin(2.0 * math.pi * frequency_hz * index / sample_rate)
            samples.extend(pcm16(value))
    samples.extend(bytes(edge_samples * 2))

    with wave.open(str(destination), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(sample_rate)
        output.writeframes(samples)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("output_directory", type=Path)
    parser.add_argument("--sample-rate", type=int, default=DEFAULT_SAMPLE_RATE)
    arguments = parser.parse_args()
    if arguments.sample_rate <= 0:
        parser.error("--sample-rate must be positive")
    arguments.output_directory.mkdir(parents=True, exist_ok=True)
    for frequency in (440, 1000, 2000):
        write_sine(arguments.output_directory / f"sine_{frequency}hz_5s.wav", frequency, arguments.sample_rate)
    write_tone_burst(arguments.output_directory / "burst_1000hz_100ms.wav", 1000, sample_rate=arguments.sample_rate)
    write_tone_burst(
        arguments.output_directory / "burst_1000hz_100ms_delayed.wav",
        1000,
        leading_silence_seconds=3,
        trailing_silence_seconds=3,
        sample_rate=arguments.sample_rate,
    )
    write_tone_burst(
        arguments.output_directory / "burst_1000hz_1s_delayed.wav",
        1000,
        burst_seconds=1,
        leading_silence_seconds=3,
        trailing_silence_seconds=3,
        sample_rate=arguments.sample_rate,
    )
    write_tone_step(arguments.output_directory / "step_440hz_to_1000hz.wav", arguments.sample_rate)


if __name__ == "__main__":
    main()
