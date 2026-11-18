namespace OperatorViews;

public readonly struct Bits(int value)
{
    public int Value => value;
    public static bool operator !(Bits x) => x.Value == 0;
    public static Bits operator ~(Bits x) => new(~x.Value);
    public static Bits operator &(Bits x, Bits y) => new(x.Value & y.Value);
    public static Bits operator |(Bits x, Bits y) => new(x.Value | y.Value);
    public static Bits operator ^(Bits x, Bits y) => new(x.Value ^ y.Value);
    public static Bits operator <<(Bits x, int n) => new(x.Value << n);
    public static Bits operator >>(Bits x, int n) => new(x.Value >> n);
    public static Bits operator >>>(Bits x, int n) => new(x.Value >>> n);
}

public sealed class Operand<T>(T value)
{
    public T Value => value;
    public static T operator &(Bits bits, Operand<T> rhs) => rhs.Value;
    public static T operator ~(Operand<T> rhs) => rhs.Value;
}
