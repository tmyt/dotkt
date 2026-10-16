using System.Collections.Generic;

namespace GenericValueInterop;

public interface ICollectionOverloadSlot { string Pick(object value); }
public class CollectionOverloadBase { public virtual string Pick(object value) => "base"; }

public static class CollectionStorageApi
{
    public static T[] Copy<T>(object value)
    {
        var collection = (ICollection<T>)value;
        var result = new T[collection.Count];
        collection.CopyTo(result, 0);
        return result;
    }

    public static bool Contains<T>(object value, T element) => ((ICollection<T>)value).Contains(element);
    public static bool ContainsStoredCollection<T>(object value, object element) =>
        ((ICollection<object>)value).Contains(element);
    public static IReadOnlyList<IReadOnlyCollection<T>> NativeNestedIdentity<T>(
        IReadOnlyList<IReadOnlyCollection<T>> value) => value;
    public static int IndexOf<T>(object value, T element) => ((IList<T>)value).IndexOf(element);
}
