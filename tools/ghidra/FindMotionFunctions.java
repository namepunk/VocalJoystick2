// Ghidra headless script: locate MotionExtractor references and decompile their functions.
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;

public class FindMotionFunctions extends GhidraScript {
    @Override
    public void run() throws Exception {
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        for (Data data : currentProgram.getListing().getDefinedData(true)) {
            Object value = data.getValue();
            if (value == null || !value.toString().contains("MotionExtractor")) continue;
            println("STRING " + data.getAddress() + ": " + value);
            for (Reference reference : currentProgram.getReferenceManager().getReferencesTo(data.getAddress())) {
                Function function = getFunctionContaining(reference.getFromAddress());
                if (function == null) continue;
                println("===== " + function.getName() + " @ " + function.getEntryPoint() + " =====");
                DecompileResults result = decompiler.decompileFunction(function, 120, monitor);
                if (result.decompileCompleted()) println(result.getDecompiledFunction().getC());
                else println("DECOMPILATION FAILED: " + result.getErrorMessage());
            }
        }
        decompiler.dispose();
    }
}
