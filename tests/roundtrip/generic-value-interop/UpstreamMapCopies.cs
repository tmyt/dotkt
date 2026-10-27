using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

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

    public static bool HasErasedJoinedReturn(object value, string method)
    {
        var declaration = value.GetType().Assembly
            .GetType("roundtrip.upstreamerasedstdlib.UpstreamErasedStdlibKt")!.GetMethod(method)!;
        var carrier = declaration.ReturnType;
        var source = JsonNode.Parse("""
            {"t":"fqn","name":"roundtrip.upstreamerasedstdlib.JoinedValue","args":[{"t":"tv","scope":"method","i":0}]}
            """);
        var projection = JsonNode.Parse("""
            {"t":"fqn","name":"roundtrip.upstreamerasedstdlib.JoinedValue","args":[{"t":"star"}]}
            """);
        return carrier.IsInterface && !carrier.IsGenericType && carrier.IsAssignableFrom(value.GetType())
            && carrier.GetCustomAttributesData().Any(attribute => attribute.AttributeType.FullName ==
                "System.Runtime.CompilerServices.CompilerGeneratedAttribute")
            && JsonNode.DeepEquals(KotlinType(declaration.ReturnParameter.GetCustomAttributesData()), source)
            && JsonNode.DeepEquals(KotlinType(carrier.GetCustomAttributesData()), projection);
    }

    static JsonNode KotlinType(IEnumerable<CustomAttributeData> attributes)
    {
        var attribute = attributes.Single(value => value.AttributeType.FullName ==
            "DotKt.Runtime.CompilerServices.KotlinTypeAttribute");
        if ((string)attribute.ConstructorArguments[0].Value! != "bir-json/1")
            throw new System.InvalidOperationException("Unexpected Kotlin type metadata codec");
        var bytes = ((IEnumerable<CustomAttributeTypedArgument>)attribute.ConstructorArguments[1].Value!)
            .Select(value => (byte)value.Value!).ToArray();
        return JsonNode.Parse(bytes)!;
    }

    public static bool OverrideReturnMatches(object value, string method) =>
        value.GetType().GetMethod(method)!.ReturnType == value.GetType().BaseType!.GetMethod(method)!.ReturnType;

    public static int AddToEmpty(System.Collections.Generic.Dictionary<int, string> map)
    {
        if (map.Count != 0) throw new System.InvalidOperationException("Expected an empty copy");
        map.Add(1, "one");
        return map.Count;
    }
}
