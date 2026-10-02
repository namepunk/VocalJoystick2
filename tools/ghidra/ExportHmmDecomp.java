// Ghidra headless script: focused export of one legacy HMM/GMM routine.
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;

public class ExportHmmDecomp extends GhidraScript {
    @Override
    public void run() throws Exception {
        // Reader binds the EBMM text grammar to BMM references.
        Function function = getFunctionAt(toAddr(0x100390c0L));
        if (function == null) {
            println("MISSING EBMM::read");
            return;
        }
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        println("===== " + function.getName() + " @ " + function.getEntryPoint() + " =====");
        DecompileResults result = decompiler.decompileFunction(function, 120, monitor);
        if (result.decompileCompleted()) println(result.getDecompiledFunction().getC());
        else println("DECOMPILATION FAILED: " + result.getErrorMessage());
        decompiler.dispose();
    }
}
