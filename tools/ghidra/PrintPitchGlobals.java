// Ghidra headless script: print float globals used by legacy pitch tracking.
import ghidra.app.script.GhidraScript;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

public class PrintPitchGlobals extends GhidraScript {
    @Override
    public void run() throws Exception {
        long[] addresses = {
            0x1004c250L, 0x1004c260L, 0x1004c268L, 0x1004c2b4L,
            0x1004c2b8L, 0x1004c2c0L, 0x1004c464L, 0x1004c468L,
            0x1004c470L, 0x1004c478L
        };
        for (long address : addresses) {
            byte[] bytes = getBytes(toAddr(address), 4);
            int bits = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).getInt();
            float value = Float.intBitsToFloat(bits);
            println(String.format("0x%08x int=0x%08x float=%s", address, bits, value));
        }
    }
}
