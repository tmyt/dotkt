namespace RoundtripDependentNative;

public interface IMarker<Tag> { }
public interface IKey<T> { }
public sealed class StringKey : IKey<string> { }
public static class Family { public interface IMark<Tag> { } }
public interface IJoined<T> where T : Family.IMark<string>, IMarker<string> { }
public interface ILeft { }
public interface IRight { }
public static class NullableListWriter
{
    public static void Append<X,Y>(X value, System.Collections.Generic.List<Y> items) where X : Y => items.Add(value);
    public static System.Collections.Generic.List<X> Empty<X,Y>() where X : Y => new();
}
public sealed class NullableListSink<T,Y> where T : Y
{
    public void Append<X>(X value, System.Collections.Generic.List<Y> items) where X : Y => items.Add(value);
}
public interface IJoinedValue<T> where T : ILeft, IRight { }
public static class JoinedSlot
{
    public static bool IsValue<T>(object value) where T : ILeft, IRight => value is IJoinedValue<T>;
    public static bool Holds<T>(IJoinedValue<T> owner, object item) where T : ILeft, IRight =>
        owner != null && item is T;
}
public class Base { }
public class MarkedBase : Base, IMarker<string> { }
public class FixedJoint : Base, Family.IMark<string> { }
public class FixedSlotBase<A> where A : Base, Family.IMark<string>
{
    public bool UsesJoint() => typeof(A) == typeof(FixedJoint);
}
public class Selector
{
    public int Visits { get; private set; }
    protected virtual T Visit<T>(T item, ref int count) where T : Base, IMarker<string>
    { count = ++Visits; return item; }
    public virtual T Choose<T>(T first, T last) where T : Base, IMarker<string> => first;
    public virtual Y Pair<X,Y>(X first, Y second) where X : Base, IMarker<string> where Y : Base, IMarker<string> => second;
    public virtual IKey<Y> Capture<X,Y>(X value, IKey<Y> key) where X : Base, IMarker<string> => key;
#nullable enable
    public void AppendNullable<X,Y>(X witness, System.Collections.Generic.List<Y> items, Y? value)
        where X : Base, IMarker<string> => items.Add(value!);
#nullable restore
    public virtual IKey<Y> Capture<X,Y>(X value, IKey<Y> key, ref int visits) where X : Base, IMarker<string>
    { visits++; return key; }
    public virtual T Apply<T>(T value, System.Action callback) where T : Base, IMarker<string>
    { callback(); return value; }
    public virtual T Apply<T>(T value, System.Action<int> callback) where T : Base, IMarker<string>
    { callback(42); return value; }
    public virtual T Choose<T>(T first, T last, ref int visits) where T : Base, IMarker<string>
    { visits++; return first; }
}
public sealed class Owner<Tag, T> where T : IMarker<Tag> { }
