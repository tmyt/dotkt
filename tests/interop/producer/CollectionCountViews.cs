using System.Collections;
using System.Collections.Generic;

namespace CollectionStorageInterop;

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
