namespace GenericValueInterop;

public class GenericOnlyComparable : System.IComparable<GenericOnlyComparable>
{
    public int Rank { get; }
    public GenericOnlyComparable(int rank) => Rank = rank;
    int System.IComparable<GenericOnlyComparable>.CompareTo(GenericOnlyComparable other) => Rank - other.Rank;
}

public sealed class DerivedComparable : GenericOnlyComparable
{
    public DerivedComparable(int rank) : base(rank) { }
}

public readonly struct ValueComparable : System.IComparable<ValueComparable>
{
    public int Rank { get; }
    public ValueComparable(int rank) => Rank = rank;
    public int CompareTo(ValueComparable other) => Rank - other.Rank;
}

public interface LeftComparison { }
public interface RightComparison { }
public class ComparisonArgument : System.IComparable<LeftComparison>
{
    public int CompareTo(LeftComparison other) => 0;
}
public sealed class LeftArgument : ComparisonArgument, LeftComparison { }
public sealed class RightArgument : ComparisonArgument, RightComparison { }
public sealed class BothArguments : ComparisonArgument, LeftComparison, RightComparison { }

public sealed class MultiComparable : System.IComparable<LeftComparison>, System.IComparable<RightComparison>
{
    int System.IComparable<LeftComparison>.CompareTo(LeftComparison other) => 17;
    int System.IComparable<RightComparison>.CompareTo(RightComparison other) => 29;
}

public sealed class ReversedMultiComparable : System.IComparable<RightComparison>, System.IComparable<LeftComparison>
{
    int System.IComparable<RightComparison>.CompareTo(RightComparison other) => 29;
    int System.IComparable<LeftComparison>.CompareTo(LeftComparison other) => 17;
}

public sealed class DualComparable : System.IComparable, System.IComparable<DualComparable>
{
    int System.IComparable.CompareTo(object other) => 41;
    int System.IComparable<DualComparable>.CompareTo(DualComparable other) => 53;
}

public sealed class ThrowingComparable : System.IComparable<ThrowingComparable>
{
    public System.InvalidOperationException Failure { get; } = new("foreign comparison");
    public int CompareTo(ThrowingComparable other) => throw Failure;
}
