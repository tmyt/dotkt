#nullable enable
namespace NativeComparison;

// A separate assembly makes argument-decoder provenance observable.
public abstract class ComparableBase<T> : System.IComparable<T>
{
    public abstract int CompareTo(T? value);
}
public abstract class AnnotatedComparableBase<T> : System.IComparable<T?>
{
    public abstract int CompareTo(T? value);
}
public abstract class NullableComparableBase<T> : System.IComparable<T?> where T : struct
{
    public abstract int CompareTo(T? value);
}
