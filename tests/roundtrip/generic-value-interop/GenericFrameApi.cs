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
}
