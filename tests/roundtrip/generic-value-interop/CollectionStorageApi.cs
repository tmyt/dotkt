using System.Collections.Generic;

namespace GenericValueInterop;

public interface ICollectionOverloadSlot { string Pick(object value); }
public class CollectionOverloadBase { public virtual string Pick(object value) => "base"; }
public sealed class CaptureBox<T> where T : allows ref struct { }

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

#nullable enable
    public static void Append<T>(List<T> items, T? value) where T : notnull => items.Add(value!);
    public static int CountWith<T, W>(List<T> items, W witness)
        where T : System.IO.Stream where W : allows ref struct => items.Count;
    public static string IdentifyWith<T>(CaptureBox<T> box, T? value, System.ReadOnlySpan<char> tag)
        where T : System.IDisposable, allows ref struct => value is null ? typeof(T).Name + ":" + tag.ToString()
            : throw new System.InvalidOperationException("Expected the supplied null");
#nullable restore
}

#nullable enable
public class ProjectedListConsumer
{
    protected void ClearWith<T>(List<T> items, System.ReadOnlySpan<char> label) where T : System.IO.Stream
    {
        if (label.Length != 5 || label[0] != 'v') throw new System.ArgumentException(nameof(label));
        items.Clear();
    }
}
