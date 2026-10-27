namespace GenericValueInterop;

public class GenericOnlyComparable : System.IComparable<GenericOnlyComparable>
{
    public int Rank { get; }
    public GenericOnlyComparable(int rank) => Rank = rank;
    int System.IComparable<GenericOnlyComparable>.CompareTo(GenericOnlyComparable other) => Rank - other.Rank;
}

public class NativeTextComparisonBase : System.IComparable<string>
{
    public int CompareTo(string other) => 77;
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

public sealed class NullAwareComparable : System.IComparable<NullAwareComparable>
{
    public int CompareTo(NullAwareComparable other) => other is null ? 1 : 0;
}

public sealed class NullableValueComparable : System.IComparable<int?>
{
    public int CompareTo(int? other) => other ?? 17;
}

public sealed class NonNullableValueComparable : System.IComparable<int>
{
    public int CompareTo(int other) => other + 1;
}

public abstract class NativeComparisonBase
{
    public NativeComparisonBase() { }
    public abstract System.IComparable Apply(System.IComparable value);
    public abstract System.IComparable Item { get; set; }
}

public static class NativeComparisonApi
{
    public static System.IComparable Echo(System.IComparable value) => value;
    public static bool ReplaceAliased(ref System.IComparable first, ref System.IComparable second,
        System.IComparable next)
    {
        first = next;
        return object.ReferenceEquals(second, next);
    }
    public static void CopyOut(out System.IComparable value, System.IComparable next) => value = next;
    public static ref System.IComparable Address(ref System.IComparable value) => ref value;
    public static bool HasNativeShape(NativeComparisonBase value)
    {
        var method = value.GetType().GetMethod("Apply", new[] { typeof(System.IComparable) });
        return method?.ReturnType == typeof(System.IComparable)
            && value.GetType().GetProperty("Item")?.PropertyType == typeof(System.IComparable);
    }
    public static bool HasOpaqueSlot(object value) =>
        value.GetType().GetProperty("value")?.PropertyType == typeof(object);
}
