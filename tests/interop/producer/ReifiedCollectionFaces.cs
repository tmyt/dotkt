using System.Collections;
using System.Collections.Generic;

namespace CollectionStorageInterop;

public static class ReifiedCollectionFaces
{
    public static object Enumerable() => new BareEnumerable();
    public static object Collection() => new CollectionOnly();
    public static object ReadOnlyCollection() => new ReadOnlyCollectionOnly();
    public static object Set() => new HashSet<int> { 7, 9 };
    public static object ReadOnlySet() => new ReadOnlySetOnly();
}

public class BareEnumerable : IEnumerable<int>
{
    public IEnumerator<int> GetEnumerator() { yield return 7; yield return 9; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class ReadOnlyCollectionOnly : IReadOnlyCollection<int>
{
    public int Count => 2;
    public IEnumerator<int> GetEnumerator() { yield return 7; yield return 9; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class CollectionOnly : ICollection<int>
{
    private readonly List<int> items = new() { 7, 9 };
    public int Count => items.Count;
    public bool IsReadOnly => false;
    public void Add(int item) => items.Add(item);
    public void Clear() => items.Clear();
    public bool Contains(int item) => items.Contains(item);
    public void CopyTo(int[] array, int index) => items.CopyTo(array, index);
    public bool Remove(int item) => items.Remove(item);
    public IEnumerator<int> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class ReadOnlySetOnly : IReadOnlySet<int>
{
    private readonly HashSet<int> items = new() { 7, 9 };
    public int Count => items.Count;
    public bool Contains(int item) => items.Contains(item);
    public bool IsProperSubsetOf(IEnumerable<int> other) => items.IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<int> other) => items.IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<int> other) => items.IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<int> other) => items.IsSupersetOf(other);
    public bool Overlaps(IEnumerable<int> other) => items.Overlaps(other);
    public bool SetEquals(IEnumerable<int> other) => items.SetEquals(other);
    public IEnumerator<int> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
