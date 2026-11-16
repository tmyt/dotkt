namespace DataIdentityNative;

public record NativeRecord(int Value);

public class NativeOrdinary
{
    public int component1() => 1;
    public NativeOrdinary copy() => this;
}
