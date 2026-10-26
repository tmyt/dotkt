namespace GenericValueInterop;

public static class UpstreamMapCopies
{
    public static System.Collections.Generic.IReadOnlyCollection<int> NativeValues() =>
        new System.Collections.Generic.List<int> { 1, 2 };

    public static int ReadNativeValues(System.Collections.Generic.IReadOnlyCollection<int> values) => values.Count;

    public static System.Collections.Generic.IReadOnlyCollection<string[]> NativeNestedValues() =>
        new System.Collections.Generic.List<string[]> { new[] { "nested" } };

    public static int ReadNativeNestedValues(System.Collections.Generic.IReadOnlyCollection<string[]> values) =>
        values.Count;

    public static int[] NativeArray() => new[] { 1, 2 };

    public static int ReadNativeArray(int[] values) => values.Length;

    public static System.Func<System.Collections.Generic.IReadOnlyCollection<int>> NativeDelegate() => NativeValues;

    public static int ReadNativeDelegate(System.Func<System.Collections.Generic.IReadOnlyCollection<int>> value) =>
        value().Count;

    public static bool NativeSignaturesRemainExact() =>
        typeof(UpstreamMapCopies).GetMethod(nameof(NativeNestedValues))!.ReturnType ==
            typeof(System.Collections.Generic.IReadOnlyCollection<string[]>) &&
        typeof(UpstreamMapCopies).GetMethod(nameof(NativeArray))!.ReturnType == typeof(int[]) &&
        typeof(UpstreamMapCopies).GetMethod(nameof(NativeDelegate))!.ReturnType ==
            typeof(System.Func<System.Collections.Generic.IReadOnlyCollection<int>>);

    public static bool HasConstructedReturn(object value, string method) =>
        value.GetType().Assembly.GetType("roundtrip.upstreamerasedstdlib.UpstreamErasedStdlibKt")!
            .GetMethod(method)!.ReturnType.IsGenericType;

    public static bool OverrideReturnMatches(object value, string method) =>
        value.GetType().GetMethod(method)!.ReturnType == value.GetType().BaseType!.GetMethod(method)!.ReturnType;

    public static int AddToEmpty(System.Collections.Generic.Dictionary<int, string> map)
    {
        if (map.Count != 0) throw new System.InvalidOperationException("Expected an empty copy");
        map.Add(1, "one");
        return map.Count;
    }
}
