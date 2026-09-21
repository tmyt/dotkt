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

public sealed class IntSetOnly : ISet<int>
{
    private readonly HashSet<int> values = new() { 7, 9, 11 };
    public int Count => values.Count;
    public bool IsReadOnly => false;
    public bool Add(int item) => values.Add(item);
    void ICollection<int>.Add(int item) => values.Add(item);
    public void Clear() => values.Clear();
    public bool Contains(int item) => values.Contains(item);
    public void CopyTo(int[] array, int index) => values.CopyTo(array, index);
    public bool Remove(int item) => values.Remove(item);
    public void ExceptWith(IEnumerable<int> other) => values.ExceptWith(other);
    public void IntersectWith(IEnumerable<int> other) => values.IntersectWith(other);
    public bool IsProperSubsetOf(IEnumerable<int> other) => values.IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<int> other) => values.IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<int> other) => values.IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<int> other) => values.IsSupersetOf(other);
    public bool Overlaps(IEnumerable<int> other) => values.Overlaps(other);
    public bool SetEquals(IEnumerable<int> other) => values.SetEquals(other);
    public void SymmetricExceptWith(IEnumerable<int> other) => values.SymmetricExceptWith(other);
    public void UnionWith(IEnumerable<int> other) => values.UnionWith(other);
    public IEnumerator<int> GetEnumerator() => values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
