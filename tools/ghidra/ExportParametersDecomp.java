// Ghidra headless script: inspect legacy ParametersClass defaults without
// mixing them into the much larger MotionExtractor decompilation output.
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;

public class ExportParametersDecomp extends GhidraScript {
    @Override
    public void run() throws Exception {
        // Shared defaults include the pitch-mode and unvoiced-path settings.
        Function function = getFunctionAt(toAddr(0x100197f0L));
        if (function == null) {
            println("MISSING ParametersClass::setDefaults");
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
