// Ghidra headless script: identify callers of recovered HMM decoder entry points.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;

public class FindHmmCallers extends GhidraScript {
    @Override
    public void run() throws Exception {
        long[] targets = { 0x100048d0L, 0x100048f0L };
        for (long raw : targets) {
            Address target = toAddr(raw);
            println("===== references to " + target + " =====");
            for (Reference reference : currentProgram.getReferenceManager().getReferencesTo(target)) {
                Function caller = getFunctionContaining(reference.getFromAddress());
                println(reference.getFromAddress() + " in " + (caller == null ? "<none>" : caller.getName()));
            }
        }
    }
}
