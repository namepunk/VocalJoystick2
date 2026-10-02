// Ghidra headless script: dump nearby float/int constants used by ParametersClass defaults.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

public class PrintVadConstants extends GhidraScript {
    @Override
    public void run() throws Exception {
        for (long raw = 0x1004c270L; raw <= 0x1004c2a4L; raw += 4) {
            Address address = toAddr(raw);
            byte[] data = new byte[4];
            currentProgram.getMemory().getBytes(address, data);
            int bits = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN).getInt();
            println(address + " bits=0x" + Integer.toHexString(bits) + " int=" + bits + " float=" + Float.intBitsToFloat(bits));
        }
    }
}
