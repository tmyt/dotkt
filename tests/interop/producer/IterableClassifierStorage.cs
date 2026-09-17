using System.Collections;
using System.Collections.Generic;

namespace IterableClassifierStorage;

// Deliberately omit non-generic IDictionary: enumeration alone must not grant Kotlin Iterable identity.
public class GenericDictionary : IDictionary<string, int>
{
    private readonly IDictionary<string, int> items = new Dictionary<string, int>();
    public int this[string key] { get => items[key]; set => items[key] = value; }
    public ICollection<string> Keys => items.Keys;
    public ICollection<int> Values => items.Values;
    public int Count => items.Count;
    public bool IsReadOnly => false;
    public void Add(string key, int value) => items.Add(key, value);
    public bool ContainsKey(string key) => items.ContainsKey(key);
    public bool Remove(string key) => items.Remove(key);
    public bool TryGetValue(string key, out int value) => items.TryGetValue(key, out value);
    public void Add(KeyValuePair<string, int> item) => items.Add(item);
    public void Clear() => items.Clear();
    public bool Contains(KeyValuePair<string, int> item) => items.Contains(item);
    public void CopyTo(KeyValuePair<string, int>[] array, int index) => items.CopyTo(array, index);
    public bool Remove(KeyValuePair<string, int> item) => items.Remove(item);
    public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class ReadOnlyDictionary : IReadOnlyDictionary<string, int>
{
    private readonly IReadOnlyDictionary<string, int> items = new Dictionary<string, int>();
    public int this[string key] => items[key];
    public IEnumerable<string> Keys => items.Keys;
    public IEnumerable<int> Values => items.Values;
    public int Count => items.Count;
    public bool ContainsKey(string key) => items.ContainsKey(key);
    public bool TryGetValue(string key, out int value) => items.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class MultipleReadOnlyDictionaries : ReadOnlyDictionary, IReadOnlyDictionary<long, int>
{
    int IReadOnlyDictionary<long, int>.this[long key] => 0;
    IEnumerable<long> IReadOnlyDictionary<long, int>.Keys => System.Array.Empty<long>();
    IEnumerable<int> IReadOnlyDictionary<long, int>.Values => System.Array.Empty<int>();
    int IReadOnlyCollection<KeyValuePair<long, int>>.Count => 0;
    bool IReadOnlyDictionary<long, int>.ContainsKey(long key) => false;
    bool IReadOnlyDictionary<long, int>.TryGetValue(long key, out int value) { value = 0; return false; }
    IEnumerator<KeyValuePair<long, int>> IEnumerable<KeyValuePair<long, int>>.GetEnumerator() =>
        ((IEnumerable<KeyValuePair<long, int>>)System.Array.Empty<KeyValuePair<long, int>>()).GetEnumerator();
}

public class SetAndDictionary : HashSet<int>, IReadOnlyDictionary<string, int>
{
    int IReadOnlyDictionary<string, int>.this[string key] => 0;
    IEnumerable<string> IReadOnlyDictionary<string, int>.Keys => System.Array.Empty<string>();
    IEnumerable<int> IReadOnlyDictionary<string, int>.Values => System.Array.Empty<int>();
    int IReadOnlyCollection<KeyValuePair<string, int>>.Count => 0;
    bool IReadOnlyDictionary<string, int>.ContainsKey(string key) => false;
    bool IReadOnlyDictionary<string, int>.TryGetValue(string key, out int value) { value = 0; return false; }
    IEnumerator<KeyValuePair<string, int>> IEnumerable<KeyValuePair<string, int>>.GetEnumerator() =>
        ((IEnumerable<KeyValuePair<string, int>>)System.Array.Empty<KeyValuePair<string, int>>()).GetEnumerator();
}

public class ListAndDictionary : List<int>, IReadOnlyDictionary<string, int>
{
    int IReadOnlyDictionary<string, int>.this[string key] => 0;
    IEnumerable<string> IReadOnlyDictionary<string, int>.Keys => System.Array.Empty<string>();
    IEnumerable<int> IReadOnlyDictionary<string, int>.Values => System.Array.Empty<int>();
    int IReadOnlyCollection<KeyValuePair<string, int>>.Count => 0;
    bool IReadOnlyDictionary<string, int>.ContainsKey(string key) => false;
    bool IReadOnlyDictionary<string, int>.TryGetValue(string key, out int value) { value = 0; return false; }
    IEnumerator<KeyValuePair<string, int>> IEnumerable<KeyValuePair<string, int>>.GetEnumerator() =>
        ((IEnumerable<KeyValuePair<string, int>>)System.Array.Empty<KeyValuePair<string, int>>()).GetEnumerator();
}
