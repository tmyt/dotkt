using System;

namespace NativeArrayBaseInterop;

public static class Api
{
    public static void Clear(Array value, object original, string component)
    {
        if (!ReferenceEquals(value, original) || value.GetType().GetElementType().FullName != component)
            throw new InvalidOperationException("Array conversion changed identity or component");
        Array.Clear(value, 0, value.Length);
    }

    public static string Select(Array value) => "array";
    public static string Select(object value) => "object";
    public static Array Echo(Array value) => value;
}
