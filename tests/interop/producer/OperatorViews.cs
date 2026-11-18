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

public sealed class StrictOrder(int key)
{
    public int Key => key;
    public static string Trace = "";
    public static StrictOrder Make(int n) { Trace += n; return new(n); }
    public static bool operator <(StrictOrder a, StrictOrder b) { Trace += "<"; return a.Key < b.Key; }
    public static bool operator >(StrictOrder a, StrictOrder b) { Trace += ">"; return a.Key > b.Key; }
    public static bool operator <=(StrictOrder a, StrictOrder b) => throw new System.Exception();
    public static bool operator >=(StrictOrder a, StrictOrder b) => throw new System.Exception();
}

public readonly struct PartialOrder<T>(int key)
{
    public int Key => key;
    public static string Trace = "";
    public static bool operator <=(PartialOrder<T> a, PartialOrder<T> b)
    { Trace += "L"; return a.Key >= 0 && a.Key <= b.Key; }
    public static bool operator >=(PartialOrder<T> a, PartialOrder<T> b)
    { Trace += "G"; return a.Key >= 0 && a.Key >= b.Key; }
    public string Calls => Trace;
}

public sealed class Ranked : System.IComparable<Ranked>
{
    public int CompareTo(Ranked other) => 17;
    public static bool operator <(Ranked a, Ranked b) => throw new System.Exception();
    public static bool operator >(Ranked a, Ranked b) => throw new System.Exception();
}

public sealed class Key(int n)
{
    public int Number => n;
    public Key Oblivious() => this;
    public static bool operator ==(Key x, object y) => true;
    public static bool operator !=(Key x, object y) => true;
    public override bool Equals(object other) => false;
    public override int GetHashCode() => n;
}

public sealed class Left;
public sealed class Right<T>
{
    public static bool operator ==(Left a, Right<T> b) => true;
    public static bool operator !=(Left a, Right<T> b) => true;
    public override bool Equals(object other) => false;
    public override int GetHashCode() => 0;
}
