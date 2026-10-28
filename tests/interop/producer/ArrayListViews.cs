using System;

namespace ArrayListViews;

public static class NativeArrayWrites
{
    public static int[] Integers() => [1, 2, 1];
    public static string[] Strings() => ["first", "middle", "first"];
    public static TimeSpan[] Durations() => [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)];
    public static void Write<T>(T[] values, T value) { values[0] = value; values[2] = value; }
    public static void WriteInts(int[] values, int value) { values[0] = value; values[2] = value; }
}
