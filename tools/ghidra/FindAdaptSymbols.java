// Ghidra headless script: list adaptation and per-user data functions.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;

public class FindAdaptSymbols extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] needles = { "Adapt", "User", "Vowel", "Disc", "Feature", "Data", "File", "read", "write" };
        for (Function function : currentProgram.getFunctionManager().getFunctions(true)) {
            String name = function.getName();
            for (String needle : needles) {
                if (name.contains(needle)) {
                    println(function.getEntryPoint() + " " + name);
                    break;
                }
            }
        }
    }
}
