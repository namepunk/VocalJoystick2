// Ghidra headless script: locate pitch-related data-frame and parameter symbols.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.symbol.Symbol;

public class FindSymbol extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] needles = { "FrameFeaturesData", "ParametersClass", "pitchBackTracking", "pitchDynProg" };
        for (Symbol symbol : currentProgram.getSymbolTable().getAllSymbols(true)) {
            for (String needle : needles) {
                if (!symbol.getName().contains(needle)) continue;
                println(symbol.getAddress() + " " + symbol.getName());
                break;
            }
        }
    }
}
