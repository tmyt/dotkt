using System.Collections;
using System.Collections.Generic;

namespace CollectionStorageInterop;

public static class ForeignListFaces
{
    public static object Raw() => new ArrayList { 7, 9 };
    public static object Generic() => new GenericOnlyList();
    public static object ReadOnly() => new ReadOnlyOnlyList();
    public static object ExactViews() => new ExactObjectListViews();
}

public class GenericOnlyList : IList<int>
{
    private readonly List<int> items = new() { 7, 9 };
    public int this[int index] { get => items[index]; set => items[index] = value; }
    public int Count => items.Count;
    public bool IsReadOnly => false;
    public void Add(int value) => items.Add(value);
    public void Clear() => items.Clear();
    public bool Contains(int value) => items.Contains(value);
    public void CopyTo(int[] array, int index) => items.CopyTo(array, index);
    public bool Remove(int value) => items.Remove(value);
    public int IndexOf(int value) => items.IndexOf(value);
    public void Insert(int index, int value) => items.Insert(index, value);
    public void RemoveAt(int index) => items.RemoveAt(index);
    public IEnumerator<int> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class ReadOnlyOnlyList : IReadOnlyList<int>
{
    private readonly List<int> items = new() { 7, 9 };
    public int this[int index] => items[index];
    public int Count => items.Count;
    public IEnumerator<int> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class ExactObjectListViews : IReadOnlyList<object>, IReadOnlyList<string>
{
    object IReadOnlyList<object>.this[int index] => "object-view";
    string IReadOnlyList<string>.this[int index] => index == 0 ? "string-view" : "second";
    int IReadOnlyCollection<object>.Count => 1;
    int IReadOnlyCollection<string>.Count => 2;
    IEnumerator<object> IEnumerable<object>.GetEnumerator() { yield return "object-view"; }
    IEnumerator<string> IEnumerable<string>.GetEnumerator() { yield return "string-view"; yield return "second"; }
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<object>)this).GetEnumerator();
}
