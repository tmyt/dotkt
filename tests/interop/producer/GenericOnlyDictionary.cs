using System;
using System.Collections;
using System.Collections.Generic;

namespace DictionaryContracts;

// Deliberately no non-generic IDictionary or ICollection. Every operation is a real CLR slot.
public class GenericOnlyDictionary<K, V> : IDictionary<K, V> where K : notnull
{
    private readonly Dictionary<K, V> data = new();
    public Exception CountFailure { get; set; }
    public int EnumeratorsDisposed { get; private set; }
    public V this[K key] { get => data[key]; set => data[key] = value; }
    public ICollection<K> Keys => data.Keys;
    public ICollection<V> Values => data.Values;
    public int Count { get { if (CountFailure != null) throw CountFailure; return data.Count; } }
    public bool IsReadOnly => false;
    public void Add(K key, V value) => data.Add(key, value);
    public void Add(KeyValuePair<K, V> item) => ((ICollection<KeyValuePair<K, V>>)data).Add(item);
    public void Clear() => data.Clear();
    public bool Contains(KeyValuePair<K, V> item) => ((ICollection<KeyValuePair<K, V>>)data).Contains(item);
    public bool ContainsKey(K key) => data.ContainsKey(key);
    public void CopyTo(KeyValuePair<K, V>[] array, int index) => ((ICollection<KeyValuePair<K, V>>)data).CopyTo(array, index);
    public bool Remove(K key) => data.Remove(key);
    public bool Remove(KeyValuePair<K, V> item) => ((ICollection<KeyValuePair<K, V>>)data).Remove(item);
    public bool TryGetValue(K key, out V value) => data.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<K, V>> GetEnumerator() => new Cursor(this, data.GetEnumerator());
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class Cursor : IEnumerator<KeyValuePair<K, V>>
    {
        private readonly GenericOnlyDictionary<K, V> owner;
        private readonly IEnumerator<KeyValuePair<K, V>> inner;
        public Cursor(GenericOnlyDictionary<K, V> owner, IEnumerator<KeyValuePair<K, V>> inner) {
            this.owner = owner; this.inner = inner;
        }
        public KeyValuePair<K, V> Current => inner.Current;
        object IEnumerator.Current => Current;
        public bool MoveNext() => inner.MoveNext();
        public void Reset() => inner.Reset();
        public void Dispose() { inner.Dispose(); owner.EnumeratorsDisposed++; }
    }
}

public sealed class DualDictionary : GenericOnlyDictionary<string, string>, IDictionary<int, int>
{
    private readonly Dictionary<int, int> numbers = new() { [7] = 42, [8] = 43 };
    int IDictionary<int, int>.this[int key] { get => numbers[key]; set => numbers[key] = value; }
    ICollection<int> IDictionary<int, int>.Keys => numbers.Keys;
    ICollection<int> IDictionary<int, int>.Values => numbers.Values;
    int ICollection<KeyValuePair<int, int>>.Count => numbers.Count;
    bool ICollection<KeyValuePair<int, int>>.IsReadOnly => false;
    void IDictionary<int, int>.Add(int key, int value) => numbers.Add(key, value);
    void ICollection<KeyValuePair<int, int>>.Add(KeyValuePair<int, int> value) => ((ICollection<KeyValuePair<int, int>>)numbers).Add(value);
    void ICollection<KeyValuePair<int, int>>.Clear() => numbers.Clear();
    bool ICollection<KeyValuePair<int, int>>.Contains(KeyValuePair<int, int> value) => ((ICollection<KeyValuePair<int, int>>)numbers).Contains(value);
    bool IDictionary<int, int>.ContainsKey(int key) => numbers.ContainsKey(key);
    void ICollection<KeyValuePair<int, int>>.CopyTo(KeyValuePair<int, int>[] array, int index) => ((ICollection<KeyValuePair<int, int>>)numbers).CopyTo(array, index);
    bool IDictionary<int, int>.Remove(int key) => numbers.Remove(key);
    bool ICollection<KeyValuePair<int, int>>.Remove(KeyValuePair<int, int> value) => ((ICollection<KeyValuePair<int, int>>)numbers).Remove(value);
    bool IDictionary<int, int>.TryGetValue(int key, out int value) => numbers.TryGetValue(key, out value);
    IEnumerator<KeyValuePair<int, int>> IEnumerable<KeyValuePair<int, int>>.GetEnumerator() => numbers.GetEnumerator();
}

public static class NativeDictionaries
{
    public static object Raw() => new Hashtable { ["key"] = "raw" };
    public static object Generic() => new Dictionary<string, int> { ["key"] = 42 };
}
