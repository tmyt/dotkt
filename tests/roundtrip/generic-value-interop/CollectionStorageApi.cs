using System.Collections.Generic;

namespace GenericValueInterop;

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
        ((ICollection<IReadOnlyCollection<T>>)value).Contains((IReadOnlyCollection<T>)element);
    public static int IndexOf<T>(object value, T element) => ((IList<T>)value).IndexOf(element);
}
