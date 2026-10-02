# Reference-paper notes (non-authoritative)

`input/VJ.pdf` is the 2005 paper *The Vocal Joystick: A Voice-Based
Human-Computer Interface for Individuals with Motor Impairments*. It is useful
historical context, not a specification for the 2008 Windows binaries in this
repository. No implementation decision may rely on this document without
binary or XP-oracle confirmation.

## Claims that agree with local evidence

| Paper statement | Local evidence |
| --- | --- |
| Vowel classification uses a 26-value feature frame and a 7-frame context. | The `.wts` models have 182 inputs (26 x 7); raw XP output has 26 values per record. |
| The MLP has 50 hidden units. | All local `.wts` models enumerate 50 hidden units. |
| A centred, slightly non-causal feature context is used. | The measured delta formula requires two future and two prior frames. |
| Energy, pitch, vowel classification, and discrete sounds are separate engine functions. | Exported symbols include `computePower`, pitch tracking, pattern extraction, and BMM/HMM code. |

## Leads only; do not implement from these statements

| Paper statement | Why it remains unverified or conflicts |
| --- | --- |
| PCM at 16 kHz; energy frame 25 ms / step 10 ms; pitch frame 40 ms / step 10 ms. | Current DLL processing code uses literal 960-sample input frames, advances by 160 samples, and invokes static MFCC processing with 400 samples. `VJEngine::Reset` directly configures microphone input as 16 kHz mono, confirming 60 ms frame / 10 ms hop / 25 ms MFCC window for the XP microphone-cable oracle. The pitch-window semantics still need recovery. |
| The 26 values are described simply as MFCCs. | XP traces plus the local norms show 13 static values and 13 measured delta values. |
| Motion formula uses beta 0.6. | The packaged `prm.cfg` sets `beta 0.5`; it is a different version/configuration. |
| Four-class system details and mappings. | Local distribution contains 4, 5, 8, and 9 class models; mappings must come from packaged data and binary behaviour. |

## Use

The paper is a search index: it tells us where to inspect the binary and which
experiment might distinguish alternatives. The evidence ledger in
`legacy-analysis.md` and `reverse-engineering.md` remains authoritative for
this reconstruction.
