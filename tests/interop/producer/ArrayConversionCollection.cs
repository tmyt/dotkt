using System;
using System.Collections;
using System.Collections.Generic;

namespace ArrayConversionInterop;

public sealed class CountedCollection<T> : IReadOnlyCollection<T>
{
    private readonly T[] values;
    public CountedCollection(T[] values) => this.values = values;
    public int Count => values.Length;
    public int Enumerations { get; private set; }
    public IEnumerator<T> GetEnumerator()
    {
        Enumerations++;
        return ((IEnumerable<T>)values).GetEnumerator();
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class Api
{
    public static CountedCollection<int> Integers() => new(new[] { 7, 9 });
    public static CountedCollection<string> Names() => new(new[] { "first", "second" });
    public static CountedCollection<object> NullableIntegers() => new(new object[] { null, 7 });
    public static CountedCollection<TimeSpan> Durations() => new(new[] { TimeSpan.FromSeconds(5) });
    public static int SumIntegers<T>(T[] values) => (int)(object)values[0] + (int)(object)values[1];
    public static string FirstName(string[] values) => values[0];
    public static double FirstDuration(TimeSpan[] values) => values[0].TotalSeconds;
    public static string Component(object value) => ((Array)value).GetType().GetElementType().FullName;
}
