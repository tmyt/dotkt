using System.Collections;
using System.Collections.Generic;

namespace SetArgumentInterop;

public sealed class IntSetAndList<T> : HashSet<int>, IReadOnlyList<T>
{
    public IntSetAndList() { Add(7); Add(9); Add(11); }
    int IReadOnlyCollection<T>.Count => 2;
    T IReadOnlyList<T>.this[int index] => default!;
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)new T[2]).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => new[] { "raw-list" }.GetEnumerator();
}

public sealed class StringSetAndList<T> : HashSet<string>, IReadOnlyList<T>
{
    public StringSetAndList() { Add("set-a"); Add("set-b"); Add("set-c"); }
    int IReadOnlyCollection<T>.Count => 2;
    T IReadOnlyList<T>.this[int index] => default!;
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)new T[2]).GetEnumerator();
}

public class MutableOnlySet<T> : ISet<T>
{
    private readonly HashSet<T> values;
    public MutableOnlySet(IEnumerable<T> initial) { values = new(initial); }
    public int Count => values.Count;
    public bool IsReadOnly => false;
    public bool Add(T item) => values.Add(item);
    void ICollection<T>.Add(T item) => values.Add(item);
    public void Clear() => values.Clear();
    public bool Contains(T item) => values.Contains(item);
    public void CopyTo(T[] array, int index) => values.CopyTo(array, index);
    public bool Remove(T item) => values.Remove(item);
    public void ExceptWith(IEnumerable<T> other) => values.ExceptWith(other);
    public void IntersectWith(IEnumerable<T> other) => values.IntersectWith(other);
    public bool IsProperSubsetOf(IEnumerable<T> other) => values.IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<T> other) => values.IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<T> other) => values.IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<T> other) => values.IsSupersetOf(other);
    public bool Overlaps(IEnumerable<T> other) => values.Overlaps(other);
    public bool SetEquals(IEnumerable<T> other) => values.SetEquals(other);
    public void SymmetricExceptWith(IEnumerable<T> other) => values.SymmetricExceptWith(other);
    public void UnionWith(IEnumerable<T> other) => values.UnionWith(other);
    public IEnumerator<T> GetEnumerator() => values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class IntSetOnly : MutableOnlySet<int>
{
    public IntSetOnly() : base(new[] { 7, 9, 11 }) { }
}

public sealed class StringSetOnly : MutableOnlySet<string>
{
    public StringSetOnly() : base(new[] { "a", "b" }) { }
}

public sealed class ObjectSetOnly : MutableOnlySet<object>
{
    public ObjectSetOnly() : base(new object[] { "a", "b" }) { }
}

public sealed class ObjectAndIntSet : HashSet<int>, IReadOnlySet<object>
{
    public ObjectAndIntSet() { Add(7); Add(9); Add(11); }
    int IReadOnlyCollection<object>.Count => 2;
    IEnumerator<object> IEnumerable<object>.GetEnumerator() =>
        ((IEnumerable<object>)new object[] { "a", "b" }).GetEnumerator();
    bool IReadOnlySet<object>.Contains(object value) => value is string s && (s == "a" || s == "b");
    bool IReadOnlySet<object>.IsProperSubsetOf(IEnumerable<object> other) => false;
    bool IReadOnlySet<object>.IsProperSupersetOf(IEnumerable<object> other) => false;
    bool IReadOnlySet<object>.IsSubsetOf(IEnumerable<object> other) => false;
    bool IReadOnlySet<object>.IsSupersetOf(IEnumerable<object> other) => false;
    bool IReadOnlySet<object>.Overlaps(IEnumerable<object> other) => false;
    bool IReadOnlySet<object>.SetEquals(IEnumerable<object> other) => false;
}
