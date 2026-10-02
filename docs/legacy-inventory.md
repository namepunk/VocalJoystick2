# Legacy inventory

This is a read-only inventory of `1_VocalJoystick/SourceDir`, generated during
the first forensic pass on 2026-09-24. The supplied legacy tree is not a build
input or a runtime dependency of the future application.

## Executables and libraries

| Path | Observed role | Evidence |
| --- | --- | --- |
| `vjapp/bin/win/vj.exe` | Main legacy controller | Imports `libvj.dll`; exports listener-related symbols in its strings. |
| `vjapp/bin/win/frontend.exe` | Offline feature-reference utility | Strings describe MFCC output, feature selection, and collection modes `speech`, `vowel`, and `discrete`. |
| `vjapp/bin/win/adapt.exe` | MLP adaptation tool | Its command-line strings name input normalization/weights and output weights. |
| `vjapp/bin/win/RSCAdapt.exe` | RSC adaptation tool | Its command-line strings name `iparam` and `oparam`. |
| `vjapp/bin/win/MLRSCAdapt.exe` | ML-RSC adaptation tool | Its command-line strings name `iparam`, `oparam`, and input-to-hidden distortion. |
| `vjapp/bin/win/hmmadapt.exe` | HMM adaptation tool | Its command-line strings name BMM, EBMM, and `normType`. |
| `vjlib/lib/win32/libvj.dll` | Native C++ signal-processing and recognition core | 32-bit PE DLL; imports PortAudio, libsndfile, WinMM, User32, and VC90 CRT. |
| `vjlib/lib/win32/VJ.Engine.dll` | C++/CLI bridge/control panel | 32-bit CLR PE DLL; imports `libvj.dll` and VC90 runtime. |

The binaries are x86 PE files. The native DLL is not suitable as a dependency
of the new application; it is evidence for controlled analysis only.

## Configuration and reference data

`vjlib/data/params/prm.cfg` directly establishes:

```text
--velMode 1
--beta 0.5
--gamma 1.0
--offset 0.0
--maxvel 10.0
--classMode 2
--doClick
--minzcross 0.5
--pthresh 0.7
--f0Mode 0
--unvBias 0.3
--unvSlope 0.03
--unvLen 10
--vstart 3
--keyDelay 200
```

`libvj.dll` contains the matching class-mode string: `0: NN; 1: PSD; 2:
ML-RSC`. Therefore the shipped configuration selects ML-RSC.

The supplied WAV files are mono, 16-bit PCM at 44,100 Hz. They are training or
reference recordings, not proof of the internal feature-processing sample
rate.

## Model families

| Family | Files | Directly observed header |
| --- | --- | --- |
| MLP | `*.wts` | Text: input `182`, hidden `50`, output `4`, `5`, `8`, or `9`. |
| RSC | `*.rsc` | Binary LE: `26`, `182`, class count. |
| ML-RSC | `*.mlrsc` | Binary LE: `26`, `182`, hidden `150` (4/5 classes) or `100` (8/9), class count. |
| Acoustic GMM/BMM | `vj.hmmf` | Text: `26` features and `126` mixtures. |
| Word EBMM graph | `vj.dict` | `EBMM_MPF`, two pronunciations: `k k` and `ch C`. |

The default user copies for `s4.rsc`, `s4.mlrsc` (and equivalently sampled
families) byte-match their `adaptation/good_mono_*` base models. This must not
be generalized to a trained user profile without checking that profile.

## Adaptation workflow

`adapt.bat` calls MLP, RSC, ML-RSC, HMM, power, and pitch adaptation separately
and writes results under `vjlib/data/users/<user>/`. It combines per-vowel
power files, writes a discrete zero-crossing file, and never modifies the
adaptation base files. This supports the modern design of immutable base models
plus independently regenerable user profiles.

## Tooling

`tools/model_inspector.py` is a read-only local inspector. It checks MLP block
cardinalities, normalization cardinality, the recovered RSC/ML-RSC payload
blocks, the EBMM dictionary, and text HMMF collection metadata.

Example:

```bash
python3 tools/model_inspector.py 1_VocalJoystick/SourceDir/vjlib/data/adaptation/good_mono_4N.mlrsc
```
