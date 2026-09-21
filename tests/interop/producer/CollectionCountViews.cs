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
