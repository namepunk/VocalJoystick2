// Ghidra headless script: export the legacy VAD/pattern state-machine entry points.
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;

public class ExportPatternDecomp extends GhidraScript {
    @Override
    public void run() throws Exception {
        long[] addresses = { 0x1003cc50L, 0x10004600L, 0x100046a0L };
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        for (long address : addresses) {
            Function function = getFunctionContaining(toAddr(address));
            if (function == null) {
                println("MISSING " + Long.toHexString(address));
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
