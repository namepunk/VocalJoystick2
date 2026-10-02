// Ghidra headless script: export the recovered pitch/voicing and pattern handlers.
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;

public class ExportPitchDecomp extends GhidraScript {
    @Override
    public void run() throws Exception {
        long[] targets = {
            0x1002e3e0L, // pitch candidate generation for one frame
            0x1002d910L, // mode-0 pitch transition tuning
            0x1002d9e0L, // pitch frame/candidate setup
            0x1002e1e0L, // pitch transition helper
            0x1002ed40L, // FrameData::autoCorrelation
            0x1002f2b0L, // FrameData constructor
            0x1002eb10L, // FrameData::normCrossCorr
            0x1002dab0L, // frame-local pitch candidate costs
            0x100215c0L, // local maxima extraction
            0x1002ddc0L, // pitch transition-cost generation
            0x1003c470L,
            0x1003ce20L,
            0x100365c0L, // continuous-sound classifier update
            0x100366a0L, // continuous-sound classifier flush
            0x10036750L, // discrete-sound classifier called at StartOfSilence
            0x1003e4a0L, // post-HMM discrete command selector
            0x1003dc40L, // PatternExtractor::startOfSilence
            0x1003dd50L
        };
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        for (long target : targets) {
            Function function = getFunctionContaining(toAddr(target));
            if (function == null) {
                println("MISSING " + Long.toHexString(target));
                continue;
            }
            println("===== " + function.getName() + " @ " + function.getEntryPoint() + " =====");
            DecompileResults result = decompiler.decompileFunction(function, 120, monitor);
            if (result.decompileCompleted()) println(result.getDecompiledFunction().getC());
            else println("DECOMPILATION FAILED: " + result.getErrorMessage());
        }
        decompiler.dispose();
    }
}
