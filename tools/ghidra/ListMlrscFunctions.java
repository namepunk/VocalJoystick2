// Ghidra headless script: list named ML-RSC routines and their entry points.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;

public class ListMlrscFunctions extends GhidraScript {
    @Override
    public void run() throws Exception {
        for (Function function : currentProgram.getFunctionManager().getFunctions(true)) {
            String name = function.getName().toLowerCase();
            String signature = function.getSignature().toString().toLowerCase();
            if (name.contains("mlrsc") || signature.contains("mlrsc")) {
                println(function.getEntryPoint() + " | " + function.getSignature());
            }
        }
    }
}
