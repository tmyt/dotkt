using System;
using System.Collections.Generic;

namespace ArrayListViews;

public static class NativeArrayWrites
{
    public static int[] Integers() => [1, 2, 1];
    public static string[] Strings() => ["first", "middle", "first"];
    public static TimeSpan[] Durations() => [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)];
    public static void Write<T>(T[] values, T value) { values[0] = value; values[2] = value; }
    public static void WriteInts(int[] values, int value) { values[0] = value; values[2] = value; }
    public static T First<T>(IReadOnlyList<T> values) => values[0];
    public static int Sum(IReadOnlyList<int> values)
    {
        if (values.Count != 3) throw new InvalidOperationException("Unexpected view size");
        int total = 0;
        foreach (int value in values) total += value;
        return total;
    }
}
