using System;
using System.Collections;
using System.Collections.Generic;

namespace MutableListInterop;

public sealed class MutableAndReadOnlyList : List<int>, IReadOnlyList<string>
{
    public MutableAndReadOnlyList() { Add(7); Add(9); }
    int IReadOnlyCollection<string>.Count => 3;
    string IReadOnlyList<string>.this[int index] => "other";
    IEnumerator<string> IEnumerable<string>.GetEnumerator() => throw new NotSupportedException();
}

public sealed class SameElementList : List<int>, IReadOnlyList<int>
{
    public SameElementList() { Add(7); Add(9); }
    int IReadOnlyCollection<int>.Count => 3;
    int IReadOnlyList<int>.this[int index] => 100 + index;
}

public sealed class RawMutableList : ArrayList
{
    public RawMutableList() { Add(7); Add(9); }
}

public sealed class DualMutableList : List<int>, IList<object>
{
    private readonly List<object> objects = new() { "a", "b", "c" };
    int ICollection<object>.Count => objects.Count;
    bool ICollection<object>.IsReadOnly => false;
    object IList<object>.this[int index] { get => objects[index]; set => objects[index] = value; }
    public DualMutableList() { Add(7); Add(9); }
    void ICollection<object>.Add(object value) => objects.Add(value);
    void ICollection<object>.Clear() => objects.Clear();
    bool ICollection<object>.Contains(object value) => objects.Contains(value);
    void ICollection<object>.CopyTo(object[] array, int index) => objects.CopyTo(array, index);
    bool ICollection<object>.Remove(object value) => objects.Remove(value);
    int IList<object>.IndexOf(object value) => objects.IndexOf(value);
    void IList<object>.Insert(int index, object value) => objects.Insert(index, value);
    void IList<object>.RemoveAt(int index) => objects.RemoveAt(index);
    IEnumerator<object> IEnumerable<object>.GetEnumerator() => objects.GetEnumerator();
}

public sealed class ThrowingMutableList : List<int>, IList<int>
{
    int ICollection<int>.Count => throw new InvalidOperationException("mutable-count");
    int IList<int>.this[int index]
    {
        get => throw new InvalidOperationException("mutable-get");
        set => throw new NotSupportedException();
    }
}
