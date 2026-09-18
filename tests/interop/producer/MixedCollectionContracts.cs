using System.Collections;
using System.Collections.Generic;

namespace MixedCollectionContracts;

public class ListWithMutableDictionary : List<int>, IDictionary<string, int>
{
    int IDictionary<string, int>.this[string key] { get => 0; set { } }
    ICollection<string> IDictionary<string, int>.Keys => System.Array.Empty<string>();
    ICollection<int> IDictionary<string, int>.Values => System.Array.Empty<int>();
    void IDictionary<string, int>.Add(string key, int value) { }
    bool IDictionary<string, int>.ContainsKey(string key) => false;
    bool IDictionary<string, int>.Remove(string key) => false;
    bool IDictionary<string, int>.TryGetValue(string key, out int value) { value = 0; return false; }
    int ICollection<KeyValuePair<string, int>>.Count => 0;
    bool ICollection<KeyValuePair<string, int>>.IsReadOnly => false;
    void ICollection<KeyValuePair<string, int>>.Add(KeyValuePair<string, int> item) { }
    void ICollection<KeyValuePair<string, int>>.Clear() { }
    bool ICollection<KeyValuePair<string, int>>.Contains(KeyValuePair<string, int> item) => false;
    void ICollection<KeyValuePair<string, int>>.CopyTo(KeyValuePair<string, int>[] array, int index) { }
    bool ICollection<KeyValuePair<string, int>>.Remove(KeyValuePair<string, int> item) => false;
    IEnumerator<KeyValuePair<string, int>> IEnumerable<KeyValuePair<string, int>>.GetEnumerator() =>
        ((IEnumerable<KeyValuePair<string, int>>)System.Array.Empty<KeyValuePair<string, int>>()).GetEnumerator();
}

public class ListWithRawDictionary : List<int>, IDictionary
{
    object IDictionary.this[object key] { get => null; set { } }
    ICollection IDictionary.Keys => System.Array.Empty<object>();
    ICollection IDictionary.Values => System.Array.Empty<object>();
    bool IDictionary.IsReadOnly => false;
    bool IDictionary.IsFixedSize => false;
    void IDictionary.Add(object key, object value) { }
    void IDictionary.Clear() { }
    bool IDictionary.Contains(object key) => false;
    void IDictionary.Remove(object key) { }
    IDictionaryEnumerator IDictionary.GetEnumerator() => new Hashtable().GetEnumerator();
}
