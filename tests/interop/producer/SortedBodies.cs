using System;
using System.Collections;
using System.Collections.Generic;

namespace SortedBodies;

public static class NativeSortInputs
{
    private sealed class Values<T>(params T[] values) : IReadOnlyCollection<T>
    {
        public int Count => values.Length;
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    public static IReadOnlyCollection<int> Integers() => new Values<int>(3, 1, 2);
    public static IReadOnlyCollection<string> Strings() => new Values<string>("c", "a", "b");
    public static IReadOnlyCollection<TimeSpan> Durations() => new Values<TimeSpan>(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));
}
