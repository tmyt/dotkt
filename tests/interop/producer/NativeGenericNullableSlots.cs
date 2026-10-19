namespace NativeGenericNullableSlots;

public interface Source<T> { T Item { get; } }
public interface DerivedSource<T> : Source<T> { }
public interface FixedSource : Source<long?> { }
public interface MutableSource<T> { T Item { get; set; } }
public abstract class BaseSource<T> { public abstract T Item { get; } }
public abstract class MidSource<A, B> : BaseSource<B> { }
public abstract class MutableBaseSource<T> { public abstract T Item { get; set; } }
public abstract class MutableMidSource<A, B> : MutableBaseSource<A> { }

public static class Api
{
    public static object ReadBoxed(object value) => ((Source<object>)value).Item;
    public static string ReadText(object value) => ((Source<string>)value).Item;
    public static long? ReadFixed(object value) => ((FixedSource)value).Item;
    public static object ReadMutable(object value) => ((MutableSource<object>)value).Item;
    public static void WriteMutable(object value, object item) => ((MutableSource<object>)value).Item = item;
    public static object ReadBase(object value) => ((BaseSource<object>)value).Item;
    public static object ReadMutableBase(object value) => ((MutableBaseSource<object>)value).Item;
    public static void WriteMutableBase(object value, object item) => ((MutableBaseSource<object>)value).Item = item;
}
