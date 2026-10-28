using System;
using System.Collections.Generic;

namespace mapsetabi;

public static class NativeMaps
{
    public static Dictionary<int, string> Integers() => new();
    public static Dictionary<string, TimeSpan> Durations() => new();
    public static Dictionary<string, object> Objects() => new();
    public static bool HasInteger(Dictionary<int, string> map, int key, string value) =>
        map.Count == 1 && map.TryGetValue(key, out var actual) && actual == value;
    public static bool HasDuration(Dictionary<string, TimeSpan> map, string key, double seconds) =>
        map.Count == 1 && map.TryGetValue(key, out var actual) && actual.TotalSeconds == seconds;
    public static bool HasNull(Dictionary<string, object> map, string key) =>
        map.ContainsKey(key) && map[key] is null;
    public static bool HasBoxedInteger(Dictionary<string, object> map, string key, int value) =>
        map.TryGetValue(key, out var actual) && actual is int number && number == value;
}
