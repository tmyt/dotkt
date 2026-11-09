using System;
using System.Collections.Generic;

namespace CollectionFaceInterop;

public interface NativeCollectionDefaults : ICollection<int>
{
    int ICollection<int>.Count => 1;
    bool ICollection<int>.IsReadOnly => true;
    bool ICollection<int>.Contains(int item) => item == 17;
    void ICollection<int>.CopyTo(int[] array, int index) { array[index] = 17; }
    void ICollection<int>.Add(int item) => throw new NotSupportedException();
    bool ICollection<int>.Remove(int item) => throw new NotSupportedException();
    void ICollection<int>.Clear() => throw new NotSupportedException();
    IEnumerator<int> IEnumerable<int>.GetEnumerator() => ((IEnumerable<int>)new[] { 17 }).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => new[] { 17 }.GetEnumerator();
}

public static class NativeFaces
{
    public static T CopyFirstAndAppend<T>(ICollection<T> source, T value)
    {
        if (source.IsReadOnly || !source.Contains(value))
            throw new InvalidOperationException("The native collection face did not dispatch correctly.");
        var target = new T[source.Count + 2];
        source.CopyTo(target, 1);
        source.Add(value);
        return target[1];
    }

    public static int IndexOf<T>(IList<T> source, T value) => source.IndexOf(value);
    public static bool IsReadOnly<T>(ICollection<T> source) => source.IsReadOnly;
}
