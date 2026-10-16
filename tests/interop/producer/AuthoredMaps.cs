using System;
using System.Collections;
using System.Collections.Generic;

namespace AuthoredMapInterop;

public static class NativeMaps
{
    public static bool ReadOnly(object source)
    {
        var map = (IDictionary<int, string>)source;
        if (map.Count != 1 || map[7] != "seven" || !map.ContainsKey(7)
            || !map.Keys.Contains(7) || !map.Values.Contains("seven") || !map.IsReadOnly) return false;
        if (map.TryGetValue(8, out var missing) || missing != null) return false;
        if (!map.TryGetValue(7, out var present) || present != "seven") return false;
        try { _ = map[8]; return false; } catch (KeyNotFoundException) { }
        try { map.Add(8, "eight"); return false; } catch (NotSupportedException) { }
        try { map.Remove(new KeyValuePair<int, string>(8, "eight")); return false; }
        catch (NotSupportedException) { }
        using var iterator = map.GetEnumerator();
        if (!iterator.MoveNext() || iterator.Current.Key != 7 || iterator.Current.Value != "seven"
            || iterator.MoveNext()) return false;
        var raw = ((IEnumerable)map).GetEnumerator();
        try { return raw.MoveNext() && raw.Current is KeyValuePair<int, string> pair
                && pair.Key == 7 && pair.Value == "seven" && !raw.MoveNext(); }
        finally { (raw as IDisposable)?.Dispose(); }
    }

    public static bool Mutable(object source)
    {
        var map = (IDictionary<int, int>)source;
        if (map.IsReadOnly || !map.TryGetValue(1, out var zero) || zero != 0) return false;
        if (map.TryGetValue(9, out var absent) || absent != 0) return false;
        var keys = map.Keys;
        var values = map.Values;
        map[2] = 42;
        if (!keys.Contains(2) || !values.Contains(42)) return false;
        try { map.Add(2, 99); return false; } catch (ArgumentException) { }
        ICollection<KeyValuePair<int, int>> pairs = map;
        if (!pairs.Contains(new(2, 42)) || pairs.Contains(new(2, 99)) || pairs.Remove(new(2, 99))) return false;
        var copy = new KeyValuePair<int, int>[map.Count + 1];
        pairs.CopyTo(copy, 1);
        var sum = 0;
        foreach (var pair in copy) sum += pair.Value;
        if (sum != 42 || !pairs.Remove(new(2, 42)) || map.Remove(2)) return false;
        pairs.Add(new(3, 7));
        if (map[3] != 7) return false;
        map.Clear();
        return map.Count == 0 && keys.Count == 0 && values.Count == 0;
    }

    public static object Echo(object source) => (IDictionary<int, int>)source;
    public static object RawDictionary() => new Hashtable { ["a"] = 1 };

    public static bool AbstractMap(object source)
    {
        var map = (IDictionary<string, int>)source;
        return map.Count == 1 && map["a"] == 1;
    }

    public static bool CopyArguments(object source)
    {
        var pairs = (ICollection<KeyValuePair<int, string>>)source;
        var empty = Array.Empty<KeyValuePair<int, string>>();
        if (!Throws<ArgumentNullException>(() => pairs.CopyTo(null, 0))
            || !Throws<ArgumentOutOfRangeException>(() => pairs.CopyTo(empty, -1))
            || !Throws<ArgumentException>(() => pairs.CopyTo(empty, 1))) return false;
        return pairs.Count == 0 || Throws<ArgumentException>(() => pairs.CopyTo(empty, 0));
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
        catch { return false; }
    }
}
