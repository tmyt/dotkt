namespace EqualityBoundary;

public sealed class Left
{
    public int Number => 1;
    public static bool operator ==(Left x, object y) => true;
    public static bool operator !=(Left x, object y) => true;
    public static bool operator ==(Left x, Right y) => false;
    public static bool operator !=(Left x, Right y) => false;
    public override bool Equals(object other) => false;
    public override int GetHashCode() => 1;
}

public sealed class Right
{
    public static bool operator ==(Left x, Right y) => true;
    public static bool operator !=(Left x, Right y) => true;
    public override bool Equals(object other) => false;
    public override int GetHashCode() => 2;
}
