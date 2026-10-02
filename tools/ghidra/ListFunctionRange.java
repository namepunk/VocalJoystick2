// Ghidra headless script: list functions around PatternExtractor handlers.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;

public class ListFunctionRange extends GhidraScript {
    @Override
    public void run() throws Exception {
        for (Function function : currentProgram.getFunctionManager().getFunctions(true)) {
            long offset = function.getEntryPoint().getOffset();
            if (offset >= 0x10036000L && offset < 0x1003e500L)
                println(function.getEntryPoint() + " " + function.getName());
        }
    }
}
