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
        var enumerator = raw.GetEnumerator();
        if (!enumerator.MoveNext() || enumerator.Key != null || enumerator.Value?.ToString() != "null") throw new Exception("Raw enumeration");
        if (((DictionaryEntry)enumerator.Current).Key != null || enumerator.Entry.Key != null) throw new Exception("DictionaryEntry");
        var pairs = new KeyValuePair<string, string>[2];
        ((ICollection)raw).CopyTo(pairs, 0);
        if (pairs[0].Key != null || pairs[0].Value != "null") throw new Exception("Pair copy");
        var entries = new DictionaryEntry[2];
        ((ICollection)raw).CopyTo(entries, 0);
        if (entries[0].Key != null) throw new Exception("Raw copy");
        var keyArray = new object[2];
        keys.CopyTo(keyArray, 0);
        if (keyArray[0] != null) throw new Exception("Keys copy");
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
    }

    public static int Use(Dictionary<string, string> source) => source.Count;
    public static int UseOrdered(OrderedDictionary<string, string> source) => source.Count;
    public static int UseNumbers(Dictionary<int, string> source) => source.Count;
    public static int UsePrimitiveValues(Dictionary<string, int> source) => source["value"];
    public static int UseFloats(Dictionary<double, string> source) =>
        source.Count == 2 && source[0.0] == "positive" && source[-0.0] == "negative" ? 2 : -1;
    public static object FirstValue(Dictionary<string, object> source) => source["value"];
}
