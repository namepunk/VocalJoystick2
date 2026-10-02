// Ghidra headless script: list all named HMM and recognizer routines.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;

public class FindHmmSymbols extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] needles = { "EBMM", "BMM", "Recognizer", "Discrete", "HMM", "discSound", "startOfSpeech", "startOfSilence", "Mouse", "Click", "Controller", "PatternData" };
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
