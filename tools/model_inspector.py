#!/usr/bin/env python3
"""Read-only inspector for Vocal Joystick legacy model and configuration files."""

from __future__ import annotations

import argparse
import hashlib
import re
import struct
from pathlib import Path


def text_lines(path: Path) -> list[str]:
    return [content for line in path.read_text(encoding="ascii").splitlines()
            if (content := line.split("#", 1)[0].strip())]


def inspect_wts(path: Path) -> list[str]:
    lines = text_lines(path)
    input_count, hidden_count, output_count = map(int, lines[:3])
    counts = list(map(int, (lines[3], lines[4 + input_count * hidden_count],
                             lines[5 + input_count * hidden_count + hidden_count * output_count],
                             lines[6 + input_count * hidden_count + hidden_count * output_count + hidden_count])))
    expected = [input_count * hidden_count, hidden_count * output_count, hidden_count, output_count]
    if counts != expected:
        raise ValueError(f"declared block sizes {counts} do not match expected {expected}")
    return [
        "format: legacy text MLP weights",
        f"dimensions: input={input_count}, hidden={hidden_count}, output={output_count}",
        f"blocks: input-to-hidden={counts[0]}, hidden-to-output={counts[1]}, hidden-bias={counts[2]}, output-bias={counts[3]}",
    ]


def inspect_norms(path: Path) -> list[str]:
    values = text_lines(path)
    count = int(values[0])
    means = list(map(float, values[1:1 + count]))
    inverse_stds_count = int(values[1 + count])
    if len(means) != count or inverse_stds_count != count:
        raise ValueError("invalid norms cardinality")
    return [f"format: text normalization", f"feature-count: {count}",
            "operation: normalized[i] = (x[i] - mean[i]) * inverseStd[i]"]


def inspect_binary_classifier(path: Path) -> list[str]:
    data = path.read_bytes()
    header_words = 4 if path.suffix.lower() == ".mlrsc" else 3
    if len(data) < header_words * 4 or (len(data) - header_words * 4) % 4:
        raise ValueError("not a whole-number float payload")
    header = struct.unpack("<" + "I" * header_words, data[:header_words * 4])
    fields = ("feature-count", "context-input-count", "hidden-count", "class-count") if header_words == 4 else ("feature-count", "context-input-count", "class-count")
    payload_floats = (len(data) - header_words * 4) // 4
    result = ["format: binary little-endian unsigned-int header followed by float32 payload",
              *[f"{name}: {value}" for name, value in zip(fields, header)],
              f"payload: {payload_floats} float32 values ({payload_floats * 4} bytes)"]
    if path.suffix.lower() == ".mlrsc":
        _, input_count, hidden_count, class_count = header
        blocks = [
            ("class matrices", class_count * hidden_count * hidden_count),
            ("class shifts", class_count * hidden_count),
            ("input-to-hidden weights", hidden_count * input_count),
            ("hidden biases", hidden_count),
        ]
        expected = sum(count for _, count in blocks)
        if payload_floats != expected:
            raise ValueError(f"ML-RSC payload has {payload_floats} floats; expected {expected}")
        result.extend([
            "blocks: " + ", ".join(f"{name}={count}" for name, count in blocks),
            "note: block order is recovered; classifier score formula remains under analysis",
        ])
    else:
        _, input_count, class_count = header
        blocks = [
            ("class matrices", class_count * input_count * input_count),
            ("class shifts", class_count * input_count),
        ]
        expected = sum(count for _, count in blocks)
        if payload_floats != expected:
            raise ValueError(f"RSC payload has {payload_floats} floats; expected {expected}")
        result.extend([
            "blocks: " + ", ".join(f"{name}={count}" for name, count in blocks),
            "note: class score is the squared norm after shift and class-matrix transform",
        ])
    return result


def inspect_dict(path: Path) -> list[str]:
    lines = text_lines(path)
    if lines[0] != "EBMM_MPF":
        raise ValueError("unexpected dictionary magic")
    pronunciation_count = int(lines[1])
    entries = []
    for index, line in enumerate(lines[2:], start=2):
        entry = line.split()
        if len(entry) == 3 and re.fullmatch(r"\d+", entry[0]):
            state_count = int(lines[index + 1])
            entries.append(f"id={entry[0]}, orthography={entry[1]}, transcription={entry[2]}, states={state_count}")
    if len(entries) != pronunciation_count:
        raise ValueError("pronunciation count does not match graph entries")
    return ["format: EBMM_MPF text pronunciation graph", f"pronunciations: {pronunciation_count}", *entries]


def inspect_hmmf(path: Path) -> list[str]:
    lines = text_lines(path)
    feature_count = int(lines[0])
    mixture_count = int(lines[1])
    raw_lines = path.read_text(encoding="ascii").splitlines()
    components = []
    for index, line in enumerate(raw_lines):
        if re.match(r"^\s*\d+\s+# Mixture number", line):
            for candidate in raw_lines[index + 1:]:
                content = candidate.split("#", 1)[0].strip()
                if content:
                    components.append(int(content))
                    break
    if len(components) != mixture_count:
        raise ValueError("mixture count does not match component declarations")
    return ["format: text Gaussian-mixture collection", f"feature-count: {feature_count}",
            f"mixture-count: {mixture_count}", f"component-counts: {components}"]


def inspect(path: Path) -> list[str]:
    suffix = path.suffix.lower()
    if suffix == ".wts":
        details = inspect_wts(path)
    elif path.name == "vj.norms":
        details = inspect_norms(path)
    elif suffix in {".rsc", ".mlrsc"}:
        details = inspect_binary_classifier(path)
    elif suffix == ".dict":
        details = inspect_dict(path)
    elif suffix == ".hmmf":
        details = inspect_hmmf(path)
    else:
        raise ValueError(f"unsupported file type: {path.name}")
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    return [f"file: {path}", f"size: {path.stat().st_size} bytes", f"sha256: {digest}", *details]


def main() -> int:
    parser = argparse.ArgumentParser(description="Inspect a legacy Vocal Joystick model without modifying it.")
    parser.add_argument("path", type=Path)
    args = parser.parse_args()
    try:
        print("\n".join(inspect(args.path)))
    except (OSError, UnicodeError, ValueError, IndexError) as error:
        parser.error(str(error))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
