// Ghidra headless script: export MotionExtractor reset and its immediate helpers.
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;

public class ExportMotionDecomp extends GhidraScript {
    private final long[] targets = {
        0x10024910L, // MotionExtractor initialization helper
        0x1003b720L, // MotionExtractor::Reset
        0x1003b5d0L, // MotionExtractor::patternDataReceived
        0x100246e0L, // helper called by patternDataReceived to derive MotionData
        0x10024400L, // velocity calculation called by the MotionData helper
        0x1002d390L  // FrameData to FrameFeaturesData conversion, including power
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
            DecompileResults result = decompiler.decompileFunction(function, 120, monitor);
            if (result.decompileCompleted()) println(result.getDecompiledFunction().getC());
            else println("DECOMPILATION FAILED: " + result.getErrorMessage());
        }
        decompiler.dispose();
    }
}
