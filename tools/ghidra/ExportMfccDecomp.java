// Ghidra headless script: export only the functions relevant to the MFCC path.
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;

public class ExportMfccDecomp extends GhidraScript {
    private final long[] targets = {
        0x1002f2d0L, // FrameData::MFCCFrontEnd
        0x10037140L, // private callee reached by MFCCFrontEnd
        0x100319f0L, // windowing stage
        0x10031ad0L, // FFT preparation/transform stage
        0x10031660L, // spectrum magnitude stage
        0x10031060L, // filterbank stage
        0x10031100L, // filterbank construction and weight generation
        0x10030f90L, // cepstral transform stage
        0x1003c750L, // FeatureExtractor::audioFrameReceived
        0x10034900L, // AudioDataReader::SetInputToMic
        0x10037980L, // FrontEnd::Reset, which chooses output handling
        0x1003d580L, // VJEngine::Reset, which configures the audio reader
        0x10022120L, // mlpNNAdapt::forwardHidden2Output
        0x10022170L, // mlpNNAdapt::forwardInput2Hidden
        0x100235b0L, // mlpNNAdapt::forwardInput2Output
        0x1002f560L, // matrix-vector multiply helper
        0x1002f6b0L, // softmax_vf_vf
        0x1002f770L, // sigmoid_vf_vf
        0x1002f040L, // FrameData::removeDC
        0x1002f130L, // FrameData::loadWave
        0x10025560L, // MLRSC_base::init, identified from its read diagnostics
        0x10036f50L, // MLRSC_classify::classify, identified from its diagnostic
        0x10015ac0L, // ML-RSC classifier helper
        0x10015660L, // ML-RSC classifier helper
        0x10025010L, // ML-RSC classifier helper
        0x10015430L, // ML-RSC classifier helper
        0x10014f20L, // ML-RSC class-matrix multiply helper
        0x100158e0L, // ML-RSC class-score helper
        0x100365c0L, // classifier-adjacent probability helper
        0x100369c0L, // ML-RSC caller that constructs its 182-value input
        0x10035e50L  // RSC classifier called by the same context builder
    };

    @Override
    public void run() throws Exception {
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        for (long target : targets) {
            Function function = getFunctionAt(toAddr(target));
            if (function == null) {
                println("MISSING FUNCTION at " + toAddr(target));
                continue;
            }
            println("===== " + function.getName() + " @ " + function.getEntryPoint() + " =====");
            DecompileResults results = decompiler.decompileFunction(function, 120, monitor);
            if (!results.decompileCompleted()) {
                println("DECOMPILATION FAILED: " + results.getErrorMessage());
                continue;
            }
            println(results.getDecompiledFunction().getC());
        }
        decompiler.dispose();
    }
}
