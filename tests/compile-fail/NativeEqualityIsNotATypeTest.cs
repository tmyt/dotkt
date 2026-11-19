namespace EqualityBoundary;

public class Outer
{
    protected class Hidden { public static int operator &(Left left, Hidden hidden) => 0; }
}

#nullable enable
public sealed class Order
{
    public static bool operator <=(Order a, Order? b) => true;
    public static bool operator >=(Order a, Order b) => b.GetHashCode() == 0;
}
#nullable restore

public sealed class Left
{
    public int Number => 1;
    public int BranchNumber => 2;
    public static bool operator ==(Left x, object y) => true;
    public static bool operator !=(Left x, object y) => true;
    public static bool operator ==(Left x, Right y) => false;
    public static bool operator !=(Left x, Right y) => false;
}

public sealed class Right
{
    public static bool operator ==(Left x, Right y) => true;
    public static bool operator !=(Left x, Right y) => true;
    public override bool Equals(object other) => false;
    public override int GetHashCode() => 2;
}
