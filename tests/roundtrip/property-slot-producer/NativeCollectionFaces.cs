using System;
using System.Collections.Generic;

namespace CollectionFaceInterop;

public static class NativeFaces
{
    public static T CopyFirstAndAppend<T>(ICollection<T> source, T value)
    {
        if (source.IsReadOnly || !source.Contains(value))
            throw new InvalidOperationException("The native collection face did not dispatch correctly.");
        var target = new T[source.Count + 2];
        source.CopyTo(target, 1);
        source.Add(value);
        return target[1];
    }

    public static int IndexOf<T>(IList<T> source, T value) => source.IndexOf(value);
    public static bool IsReadOnly<T>(ICollection<T> source) => source.IsReadOnly;
}
