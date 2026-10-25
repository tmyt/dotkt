using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace NullableMapInterop;

public static class NativeMaps
{
    public static void VerifyFaces(object source)
    {
        var map = (IDictionary<string, string>)source;
        var read = (IReadOnlyDictionary<string, string>)source;
        var raw = (IDictionary)source;
        if (map.Count != 2 || read.Count != 2 || raw.Count != 2) throw new Exception("Count");
        if (!map.ContainsKey(null) || !read.TryGetValue(null, out var value) || value != "null") throw new Exception("Null key");
        if (!raw.Contains(null) || (string)raw[null] != "null" || raw.Contains(42) || raw[42] != null) throw new Exception("Raw lookup");
        var keys = raw.Keys;
        var values = raw.Values;
        if (!keys.Cast<object>().SequenceEqual(new object[] { null, "key" }) ||
            !values.Cast<object>().SequenceEqual(new object[] { "null", null })) throw new Exception("Raw views enumeration");
        var enumerator = raw.GetEnumerator();
        Expect<InvalidOperationException>(() => _ = enumerator.Current);
        Expect<NotSupportedException>(enumerator.Reset);
        if (!enumerator.MoveNext() || enumerator.Key != null || enumerator.Value?.ToString() != "null") throw new Exception("Raw enumeration");
        if (((DictionaryEntry)enumerator.Current).Key != null || enumerator.Entry.Key != null) throw new Exception("DictionaryEntry");
        while (enumerator.MoveNext()) { }
        Expect<InvalidOperationException>(() => _ = enumerator.Current);
        var keyEnumerator = keys.GetEnumerator();
        while (keyEnumerator.MoveNext()) { }
        Expect<InvalidOperationException>(() => _ = keyEnumerator.Current);
        Expect<NotSupportedException>(keyEnumerator.Reset);
        var pairs = new KeyValuePair<string, string>[2];
        ((ICollection)raw).CopyTo(pairs, 0);
        if (pairs[0].Key != null || pairs[0].Value != "null") throw new Exception("Pair copy");
        var entries = new DictionaryEntry[2];
        ((ICollection)raw).CopyTo(entries, 0);
        if (entries[0].Key != null) throw new Exception("Raw copy");
        var keyArray = new object[2];
        keys.CopyTo(keyArray, 0);
        if (keyArray[0] != null) throw new Exception("Keys copy");
        foreach (var collection in new[] { (ICollection)raw, keys, values })
        {
            Expect<ArgumentNullException>(() => collection.CopyTo(null, 0));
            Expect<ArgumentOutOfRangeException>(() => collection.CopyTo(new object[2], -1));
            Expect<ArgumentException>(() => collection.CopyTo(new object[1], 0));
            Expect<ArgumentException>(() => collection.CopyTo(new int[2], 0));
        }
        raw[null] = "changed";
        if (read[null] != "changed" || read.Keys.First() != null) throw new Exception("Shared mutation");
        map.Add("later", null);
        if (keys.Count != 3 || values.Count != 3 || !read.Values.Contains(null)) throw new Exception("Live views");
        if (map.TryGetValue("missing", out value) || value != null) throw new Exception("Missing result");
        try { _ = read["missing"]; throw new Exception("Missing did not throw"); } catch (KeyNotFoundException) { }
        try { map.Add(null, "duplicate"); throw new Exception("Duplicate did not throw"); } catch (ArgumentException) { }
        raw.Remove(42);
        raw.Remove(null);
        if (map.ContainsKey(null) || map.Count != 2 || keys.Count != 2) throw new Exception("Raw remove");
        VerifyAddedMutation(map.GetEnumerator(), map, "generic");
        VerifyAddedMutation(read.GetEnumerator(), map, "readonly");
        VerifyAddedMutation(raw.GetEnumerator(), map, "raw");
        VerifyAddedMutation(map.Keys.GetEnumerator(), map, "generic-keys");
        VerifyAddedMutation(map.Values.GetEnumerator(), map, "generic-values");
        VerifyAddedMutation(keys.GetEnumerator(), map, "raw-keys");
        VerifyAddedMutation(values.GetEnumerator(), map, "raw-values");
    }

    private static void VerifyAddedMutation(IEnumerator enumerator, IDictionary<string, string> map, string key)
    {
        enumerator.MoveNext();
        map.Add("added-" + key, null);
        Expect<InvalidOperationException>(() => enumerator.MoveNext());
        (enumerator as IDisposable)?.Dispose();
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    public static void VerifyReadonlyFaces(object source)
    {
        var map = (IDictionary<string, string>)source;
        var read = (IReadOnlyDictionary<string, string>)source;
        var raw = (IDictionary)source;
        if (!map.IsReadOnly || !raw.IsReadOnly || !raw.IsFixedSize || read.Count != 2 || read[null] != "null")
            throw new Exception("Readonly faces");
        Expect<NotSupportedException>(() => map.Add("added", "value"));
        Expect<NotSupportedException>(() => raw.Add("added", "value"));
        Expect<NotSupportedException>(() => raw[null] = "changed");
        Expect<NotSupportedException>(() => raw.Remove(null));
        Expect<NotSupportedException>(raw.Clear);
        if (map.Count != 2 || raw.Keys.Cast<object>().First() != null) throw new Exception("Readonly mutation");
    }

    public static int Use(Dictionary<string, string> source) => source.Count;
    public static int UseOrdered(OrderedDictionary<string, string> source) => source.Count;
    public static int UseNumbers(Dictionary<int, string> source) => source.Count;
    public static int UsePrimitiveValues(Dictionary<string, int> source) => source["value"];
    public static int UseFloats(Dictionary<double, string> source) =>
        source.Count == 2 && source[0.0] == "positive" && source[-0.0] == "negative" ? 2 : -1;
    public static object FirstValue(Dictionary<string, object> source) => source["value"];
    public static object NullableKeyDictionaryView(object source) => new NullableKeyDictionary((IDictionary<string, string>)source);

    // Retain Dictionary's structural Kotlin equality contract, but expose the
    // backing map through the exact public readonly surface used by equality.
    private sealed class NullableKeyDictionary : Dictionary<string, string>, IReadOnlyDictionary<string, string>
    {
        private readonly IDictionary<string, string> source;
        public NullableKeyDictionary(IDictionary<string, string> source) => this.source = source;
        string IReadOnlyDictionary<string, string>.this[string key] => source[key];
        IEnumerable<string> IReadOnlyDictionary<string, string>.Keys => source.Keys;
        IEnumerable<string> IReadOnlyDictionary<string, string>.Values => source.Values;
        bool IReadOnlyDictionary<string, string>.ContainsKey(string key) => source.ContainsKey(key);
        bool IReadOnlyDictionary<string, string>.TryGetValue(string key, out string value) => source.TryGetValue(key, out value);
        int IReadOnlyCollection<KeyValuePair<string, string>>.Count => source.Count;
        IEnumerator<KeyValuePair<string, string>> IEnumerable<KeyValuePair<string, string>>.GetEnumerator() => source.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => source.GetEnumerator();
    }
}
