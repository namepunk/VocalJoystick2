// Ghidra headless script: trace code references to pitch globals and print
// their containing functions to locate initialization and update sites.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;

public class TracePitchGlobals extends GhidraScript {
    @Override
    public void run() throws Exception {
        long[] addresses = {
            0x1004c250L, 0x1004c260L, 0x1004c268L, 0x1004c2b4L,
            0x1004c2b8L, 0x1004c2c0L, 0x1004c464L
        };
        for (long raw : addresses) {
            Address address = toAddr(raw);
            println("===== " + address + " =====");
            for (Reference reference : getReferencesTo(address)) {
                Address from = reference.getFromAddress();
                Function function = getFunctionContaining(from);
                println(reference.getReferenceType() + " from " + from + " in " +
                    (function == null ? "<no function>" : function.getName() + " @ " + function.getEntryPoint()));
            }
        }
    }
}
