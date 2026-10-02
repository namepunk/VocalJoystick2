// Ghidra headless script: locate legacy parser branches for pitch parameters.
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;

public class FindPitchParameterReferences extends GhidraScript {
    @Override
    public void run() throws Exception {
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        for (Data data : currentProgram.getListing().getDefinedData(true)) {
            Object value = data.getValue();
            if (value == null) continue;
            String text = value.toString();
            if (!text.contains("unvBias") && !text.contains("unvSlope") && !text.contains("unvLen") && !text.contains("f0Mode")) continue;
            println("STRING " + data.getAddress() + ": " + text);
            for (Reference reference : currentProgram.getReferenceManager().getReferencesTo(data.getAddress())) {
                Function function = getFunctionContaining(reference.getFromAddress());
                if (function == null) continue;
                println("===== " + function.getName() + " @ " + function.getEntryPoint() + " =====");
                DecompileResults result = decompiler.decompileFunction(function, 120, monitor);
                if (result.decompileCompleted()) {
                    String source = result.getDecompiledFunction().getC();
                    for (String needle : new String[] { "f0Mode", "unvBias", "unvSlope", "unvLen" }) {
                        int index = source.indexOf(needle);
                        if (index >= 0) {
                            int start = Math.max(0, index - 700);
                            int end = Math.min(source.length(), index + 900);
                            println("--- " + needle + " ---\n" + source.substring(start, end));
                        }
                    }
                }
            }
        }
        decompiler.dispose();
    }
}
