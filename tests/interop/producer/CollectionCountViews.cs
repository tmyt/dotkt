using System.Collections;
using System.Collections.Generic;

namespace CollectionStorageInterop;

public class MutableListAndReadOnlyCollection : IList<int>, IReadOnlyCollection<string>
{
    private readonly List<int> items = new() { 1, 2 };
    public int Count => items.Count;
    public bool IsReadOnly => false;
    public int this[int index] { get => items[index]; set => items[index] = value; }
    public void Add(int x) => items.Add(x);
    public void Clear() => items.Clear();
    public bool Contains(int x) => items.Contains(x);
    public void CopyTo(int[] a, int i) => items.CopyTo(a, i);
    public bool Remove(int x) => items.Remove(x);
    public int IndexOf(int x) => items.IndexOf(x);
    public void Insert(int i, int x) => items.Insert(i, x);
    public void RemoveAt(int i) => items.RemoveAt(i);
    public IEnumerator<int> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    int IReadOnlyCollection<string>.Count => 3;
    IEnumerator<string> IEnumerable<string>.GetEnumerator() => throw new System.NotSupportedException();
}

public class RawAndGenericList : ArrayList, IReadOnlyList<int>
{
    int IReadOnlyCollection<int>.Count => 2;
    int IReadOnlyList<int>.this[int index] => index + 7;
    IEnumerator<int> IEnumerable<int>.GetEnumerator() => throw new System.NotSupportedException();
}

public class ObjectCollectionAndIntList : IReadOnlyCollection<object>, IReadOnlyList<int>
{
    int IReadOnlyCollection<object>.Count => 3;
    int IReadOnlyCollection<int>.Count => 2;
    int IReadOnlyList<int>.this[int index] => index;
    IEnumerator<object> IEnumerable<object>.GetEnumerator() => throw new System.NotSupportedException();
    IEnumerator<int> IEnumerable<int>.GetEnumerator() => throw new System.NotSupportedException();
    IEnumerator IEnumerable.GetEnumerator() => throw new System.NotSupportedException();
}

public sealed class AmbiguousCollectionCounts : IReadOnlyList<int>, IReadOnlyList<string>
{
    int IReadOnlyCollection<int>.Count => 2;
    int IReadOnlyCollection<string>.Count => 3;
    int IReadOnlyList<int>.this[int index] => index;
    string IReadOnlyList<string>.this[int index] => "item";
    IEnumerator<int> IEnumerable<int>.GetEnumerator() => throw new System.InvalidOperationException("must not enumerate");
    IEnumerator<string> IEnumerable<string>.GetEnumerator() => throw new System.InvalidOperationException("must not enumerate");
    IEnumerator IEnumerable.GetEnumerator() => throw new System.InvalidOperationException("must not enumerate");
}

public sealed class ListAndIndependentCollection : IReadOnlyList<int>, IReadOnlyCollection<string>
{
    int IReadOnlyCollection<int>.Count => 2;
    int IReadOnlyCollection<string>.Count => 3;
    public int this[int index] => index;
    IEnumerator<int> IEnumerable<int>.GetEnumerator() => throw new System.NotSupportedException();
    IEnumerator<string> IEnumerable<string>.GetEnumerator() => throw new System.NotSupportedException();
    IEnumerator IEnumerable.GetEnumerator() => throw new System.NotSupportedException();
}

public sealed class MutableSetAndReadOnlyList : HashSet<string>, IReadOnlyList<int>
{
    public MutableSetAndReadOnlyList() { Add("a"); Add("b"); Add("c"); }
    int IReadOnlyCollection<int>.Count => 2;
    int IReadOnlyList<int>.this[int index] => index;
    IEnumerator<int> IEnumerable<int>.GetEnumerator() => throw new System.NotSupportedException();
}

public sealed class MutableListAndMutableCollection : List<int>, ICollection<string>
{
    public MutableListAndMutableCollection() { Add(7); Add(9); }
    int ICollection<string>.Count => 3;
    bool ICollection<string>.IsReadOnly => false;
    void ICollection<string>.Add(string value) => throw new System.NotSupportedException();
    void ICollection<string>.Clear() => throw new System.NotSupportedException();
    bool ICollection<string>.Contains(string value) => false;
    void ICollection<string>.CopyTo(string[] array, int index) => throw new System.NotSupportedException();
    bool ICollection<string>.Remove(string value) => false;
    IEnumerator<string> IEnumerable<string>.GetEnumerator() => throw new System.NotSupportedException();
}

public sealed class MutableListWithDictionaryStorage : OrderedDictionary<string, int>, IList<string>
{
    private readonly List<string> values = new() { "x", "y" };
    int ICollection<string>.Count => values.Count;
    bool ICollection<string>.IsReadOnly => false;
    string IList<string>.this[int index] { get => values[index]; set => values[index] = value; }
    void ICollection<string>.Add(string value) => values.Add(value);
    void ICollection<string>.Clear() => values.Clear();
    bool ICollection<string>.Contains(string value) => values.Contains(value);
    void ICollection<string>.CopyTo(string[] array, int index) => values.CopyTo(array, index);
    bool ICollection<string>.Remove(string value) => values.Remove(value);
    int IList<string>.IndexOf(string value) => values.IndexOf(value);
    void IList<string>.Insert(int index, string value) => values.Insert(index, value);
    void IList<string>.RemoveAt(int index) => values.RemoveAt(index);
    IEnumerator<string> IEnumerable<string>.GetEnumerator() => values.GetEnumerator();
}

public sealed class RawListAndDictionary : ArrayList, IReadOnlyDictionary<string, int>
{
    public RawListAndDictionary() { Add(7); Add(9); }
    int IReadOnlyDictionary<string, int>.this[string key] => 0;
    IEnumerable<string> IReadOnlyDictionary<string, int>.Keys => System.Array.Empty<string>();
    IEnumerable<int> IReadOnlyDictionary<string, int>.Values => System.Array.Empty<int>();
    int IReadOnlyCollection<KeyValuePair<string, int>>.Count => 0;
    bool IReadOnlyDictionary<string, int>.ContainsKey(string key) => false;
    bool IReadOnlyDictionary<string, int>.TryGetValue(string key, out int value) { value = 0; return false; }
    IEnumerator<KeyValuePair<string, int>> IEnumerable<KeyValuePair<string, int>>.GetEnumerator() =>
        ((IEnumerable<KeyValuePair<string, int>>)System.Array.Empty<KeyValuePair<string, int>>()).GetEnumerator();
}
