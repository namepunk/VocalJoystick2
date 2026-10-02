// Ghidra headless script: print only pitch-related assignments in setCmdArgVals.
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;

public class ExportPitchParameterAssignments extends GhidraScript {
    @Override
    public void run() throws Exception {
        Function function = getFunctionAt(toAddr(0x1001a330L));
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        DecompileResults result = decompiler.decompileFunction(function, 120, monitor);
        if (!result.decompileCompleted()) {
            println("DECOMPILATION FAILED: " + result.getErrorMessage());
            return;
        }
        String source = result.getDecompiledFunction().getC();
        for (String needle : new String[] { "PITCH_MODE", "UNVOICED", "UNV", "this + 0x268", "this + 0x2b8" }) {
            int from = 0;
            while (true) {
                int index = source.indexOf(needle, from);
                if (index < 0) break;
                println("--- " + needle + " ---\n" + source.substring(Math.max(0, index - 500), Math.min(source.length(), index + 700)));
                from = index + needle.length();
            }
        }
        decompiler.dispose();
    }
}
