using System.Collections.Generic;

namespace GenericValueInterop;

public interface ICollectionOverloadSlot { string Pick(object value); }
public class CollectionOverloadBase { public virtual string Pick(object value) => "base"; }
#nullable enable
public class CaptureBox<T> where T : allows ref struct { internal virtual T? Read() => default; }
#nullable restore

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

#nullable disable
    public static int CountOblivious<T>(List<T> items) where T : System.IO.Stream => items.Count;

#nullable enable
    private sealed class StreamBox(System.IO.MemoryStream stream) : CaptureBox<System.IO.MemoryStream>
    { internal override System.IO.MemoryStream Read() => stream; }
    public static CaptureBox<System.IO.MemoryStream> BoxStream(System.IO.MemoryStream stream) => new StreamBox(stream);
    public static T? ReadWith<T>(CaptureBox<T> box, System.ReadOnlySpan<char> tag)
        where T : System.IDisposable, allows ref struct => tag.Length == 5 ? box.Read() : throw new System.ArgumentException();
    public static void Append<T>(List<T> items, T? value) where T : notnull => items.Add(value!);
    public static object NullableIntList() => new List<int?>();
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
