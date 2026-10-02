// Ghidra headless script: list recovered temporal-pattern entry points.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;

public class FindPatternSymbols extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] needles = { "Pattern", "Speech", "Discrete", "DataReceived", "FeatureExtractor", "pitch", "Pitch" };
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
