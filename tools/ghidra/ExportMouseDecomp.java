// Ghidra headless script: recover mouse controller and physical click actions.
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;

public class ExportMouseDecomp extends GhidraScript {
    @Override
    public void run() throws Exception {
        long[] targets = {
            0x1003e300L, // MouseController constructor
            0x1003e410L, // MouseController::discSoundReceived
            0x10024230L, // MouseDevice::rightClick
            0x10024240L, // MouseDevice::middleClick
            0x10024250L  // MouseDevice::leftClick
        };
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        for (long target : targets) {
            Function function = getFunctionContaining(toAddr(target));
            if (function == null) {
                println("MISSING " + Long.toHexString(target));
                continue;
            }
            println("===== " + function.getName() + " @ " + function.getEntryPoint() + " =====");
            DecompileResults result = decompiler.decompileFunction(function, 120, monitor);
            if (result.decompileCompleted()) println(result.getDecompiledFunction().getC());
            else println("DECOMPILATION FAILED: " + result.getErrorMessage());
        }
        decompiler.dispose();
    }
}
