using System;

namespace GenericValueInterop;

public static class GenericFrameApi
{
    public static bool IsStringConstruction<T>() =>
        typeof(T).IsGenericType && Array.Exists(typeof(T).GetGenericArguments(), type => type == typeof(string));

    public static T ReplaceAndObserve<T>(ref T first, ref T second, T replacement)
    {
        first = replacement;
        if (!ReferenceEquals(second, replacement))
            throw new InvalidOperationException("The two managed references must alias the same location.");
        return second;
    }

    public static T SetAndObserve<T>(ref T first, out T second, T replacement)
    {
        second = replacement;
        if (!System.Collections.Generic.EqualityComparer<T>.Default.Equals(first, replacement))
            throw new InvalidOperationException("The ref and out arguments must alias the same location.");
        return first;
    }

    public static ref T Reference<T>(ref T value) => ref value;
}

public sealed class NativeCell<T>(T value)
{
    public T Value { get; } = value;
    public T Read() => Value;
}

public interface NativeValueEcho
{
    T Echo<T>(T value);
}

public struct NativeBoundCounter : roundtrip.constructorcarrier.BoundCounter<int>
{
    int value;
    public NativeBoundCounter(int initial) => value = initial;
    public void increment() => value++;
    public int count() => value;
}
