using System.Collections.Generic;

namespace SetArgumentRoundtrip;

public sealed class SetAndDictionary : roundtrip.setarguments.AuthoredSetBase,
    IReadOnlyDictionary<string, int>
{
    private readonly Dictionary<string, int> map = new() { ["a"] = 1, ["b"] = 2 };
    int IReadOnlyCollection<KeyValuePair<string, int>>.Count => map.Count;
    public int this[string key] => map[key];
    public IEnumerable<string> Keys => map.Keys;
    public IEnumerable<int> Values => map.Values;
    public bool ContainsKey(string key) => map.ContainsKey(key);
    public bool TryGetValue(string key, out int value) => map.TryGetValue(key, out value);
    IEnumerator<KeyValuePair<string, int>> IEnumerable<KeyValuePair<string, int>>.GetEnumerator() =>
        map.GetEnumerator();
}
