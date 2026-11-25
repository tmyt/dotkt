namespace NativeIntegerSlots;

public interface ICounter
{
    nint Signed(nint value);
    nuint Unsigned(nuint value);
}

public abstract class Counter
{
    public virtual System.IntPtr Raw(System.IntPtr value) => 0;
}

public static class Driver
{
    public static bool Check(ICounter direct, Counter derived) =>
        direct.Signed(-4) == -3 && direct.Unsigned(7) == 9 && derived.Raw(11) == 14;

    public static bool AcceptNullValue(System.Collections.Generic.Dictionary<nint, string?> map) =>
        map.TryGetValue(1, out var value) && value is null;
}
