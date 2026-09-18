using System.Collections;
using System.Collections.Generic;

namespace CollectionStorageInterop;

public class ReadOnlyListDictionary : IReadOnlyList<string>, IReadOnlyDictionary<string, int>
{
    protected readonly List<string> Items = new() { "list" };
    public string this[int index] => Items[index];
    public int Count => Items.Count;
    public IEnumerator<string> GetEnumerator() => Items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    int IReadOnlyDictionary<string, int>.this[string key] => 0;
    IEnumerable<string> IReadOnlyDictionary<string, int>.Keys => System.Array.Empty<string>();
    IEnumerable<int> IReadOnlyDictionary<string, int>.Values => System.Array.Empty<int>();
    int IReadOnlyCollection<KeyValuePair<string, int>>.Count => 0;
    bool IReadOnlyDictionary<string, int>.ContainsKey(string key) => false;
    bool IReadOnlyDictionary<string, int>.TryGetValue(string key, out int value) { value = 0; return false; }
    IEnumerator<KeyValuePair<string, int>> IEnumerable<KeyValuePair<string, int>>.GetEnumerator() =>
        ((IEnumerable<KeyValuePair<string, int>>)System.Array.Empty<KeyValuePair<string, int>>()).GetEnumerator();
}

public class MutableListDictionary : ReadOnlyListDictionary, IList<string>
{
    string IList<string>.this[int index] { get => Items[index]; set => Items[index] = value; }
    public bool IsReadOnly => false;
    public void Add(string value) => Items.Add(value);
    public void Clear() => Items.Clear();
    public bool Contains(string value) => Items.Contains(value);
    public void CopyTo(string[] array, int index) => Items.CopyTo(array, index);
    public bool Remove(string value) => Items.Remove(value);
    public int IndexOf(string value) => Items.IndexOf(value);
    public void Insert(int index, string value) => Items.Insert(index, value);
    public void RemoveAt(int index) => Items.RemoveAt(index);
}

// The same element type as dictionary storage does not make a separately declared List storage.
public class EntryListDictionary : List<KeyValuePair<string, int>>, IReadOnlyDictionary<string, int>
{
    int IReadOnlyDictionary<string, int>.this[string key] => 0;
    IEnumerable<string> IReadOnlyDictionary<string, int>.Keys => System.Array.Empty<string>();
    IEnumerable<int> IReadOnlyDictionary<string, int>.Values => System.Array.Empty<int>();
    bool IReadOnlyDictionary<string, int>.ContainsKey(string key) => false;
    bool IReadOnlyDictionary<string, int>.TryGetValue(string key, out int value) { value = 0; return false; }
}

public class DerivedOrderedDictionary : OrderedDictionary<string, int> { }

public class OrderedDictionaryWithList : OrderedDictionary<string, int>, IReadOnlyList<string>
{
    string IReadOnlyList<string>.this[int index] => "list";
    int IReadOnlyCollection<string>.Count => 1;
    IEnumerator<string> IEnumerable<string>.GetEnumerator() => ((IEnumerable<string>)new[] { "list" }).GetEnumerator();
}
