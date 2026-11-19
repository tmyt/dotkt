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

public readonly struct ReadonlyOrder(int key)
{
    public int Key => key;
    public static string Trace = "";
    public static ReadonlyOrder Make(int n) { Trace += n; return new(n); }
    public static bool operator <(in ReadonlyOrder a, in ReadonlyOrder b) { Trace += "<"; return a.Key < b.Key; }
    public static bool operator >(in ReadonlyOrder a, in ReadonlyOrder b) { Trace += ">"; return a.Key > b.Key; }
    public static bool operator <=(int a, in ReadonlyOrder b) { Trace += "L"; return a <= b.Key; }
    public static bool operator >=(int a, in ReadonlyOrder b) { Trace += "G"; return a >= b.Key; }
    public static bool operator <(in ReadonlyOrder a, int b) => throw new System.Exception();
    public static bool operator >(in ReadonlyOrder a, int b) => throw new System.Exception();
    public static bool operator <(ReadonlyOrder a, int b) => a.Key < b;
    public static bool operator >(ReadonlyOrder a, int b) => a.Key > b;
    public static bool operator <(in ReadonlyOrder a, ExplicitOperators.Ticket b) => throw new System.Exception();
    public static bool operator >(in ReadonlyOrder a, ExplicitOperators.Ticket b) => throw new System.Exception();
    [System.Runtime.CompilerServices.SpecialName]
    public static bool op_LessThan(in ReadonlyOrder a, ref long n) => ++n > 0;
    [System.Runtime.CompilerServices.SpecialName]
    public static bool op_GreaterThan(in ReadonlyOrder a, ref long n) => --n > 0;
}

public sealed class BorrowLeft<T>
{
    public static bool operator <(in BorrowLeft<T> a, BorrowRight<T> b) => throw new System.Exception();
    public static bool operator >(in BorrowLeft<T> a, BorrowRight<T> b) => throw new System.Exception();
    public static bool operator <(in BorrowLeft<T> a, BorrowLeft<T> b) => throw new System.Exception();
    public static bool operator >(in BorrowLeft<T> a, BorrowLeft<T> b) => throw new System.Exception();
    public static bool operator <(BorrowLeft<T> a, in BorrowLeft<T> b) => throw new System.Exception();
    public static bool operator >(BorrowLeft<T> a, in BorrowLeft<T> b) => throw new System.Exception();
    public static bool operator <=(BorrowLeft<T> a, BorrowLeft<T> b) => true;
    public static bool operator >=(BorrowLeft<T> a, BorrowLeft<T> b) => false;
}
public sealed class BorrowRight<U>
{
    public static bool operator <(BorrowLeft<U> a, BorrowRight<U> b) => true;
    public static bool operator >(BorrowLeft<U> a, BorrowRight<U> b) => false;
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

public class Key(int n)
{
    public static bool operator <=(Left a, Key b) => false;
    public static bool operator >=(Left a, Key b) => true;
    public int Number => n;
    public Key Oblivious() => this;
    public static string Trace = "";
    public static Key Observe(string mark, Key value) { Trace += mark; return value; }
    public static bool operator ==(Key x, object y) { Trace += "="; return true; }
    public static bool operator !=(Key x, object y) { Trace += "!"; return true; }
    public override bool Equals(object other) => false;
    public override int GetHashCode() => n;
}

public readonly struct ValueKey(int value)
{
    public static bool operator ==(ValueKey x, ValueKey y) => x.Value == y.Value;
    public static bool operator !=(ValueKey x, ValueKey y) => x.Value == y.Value;
    public int Value => value;
    public override bool Equals(object other) => false;
    public override int GetHashCode() => Value;
}

public sealed class Left
{
    public static bool operator <(Left a, Key b) => true;
    public static bool operator >(Left a, Key b) => false;
}
public sealed class Right<T>
{
    public static bool operator ==(long a, Right<T> b) => a == 42;
    public static bool operator !=(long a, Right<T> b) => a != 42;
    public static bool operator ==(Left a, Right<T> b) => true;
    public static bool operator !=(Left a, Right<T> b) => true;
    public override bool Equals(object other) => false;
    public override int GetHashCode() => 0;
}
