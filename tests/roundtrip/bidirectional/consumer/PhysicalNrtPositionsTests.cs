using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using NUnit.Framework;
using roundtrip.physicalnrtpositions;

public class PhysicalNrtPositionsTests
{
    private static readonly NullabilityInfoContext Context = new();
    private static MethodInfo Method(string name) => typeof(PhysicalNrtPositionsKt).GetMethod(name)!;
    private static void AssertPair(NullabilityInfo info, NullabilityState second)
    {
        Assert.That(info.ReadState, Is.EqualTo(NullabilityState.NotNull));
        Assert.That(info.GenericTypeArguments.Length, Is.EqualTo(2));
        Assert.That(info.GenericTypeArguments[0].Type, Is.EqualTo(typeof(IComparable)));
        Assert.That(info.GenericTypeArguments[0].ReadState, Is.EqualTo(NullabilityState.Nullable));
        Assert.That(info.GenericTypeArguments[1].ReadState, Is.EqualTo(second));
    }

    [Test]
    public void ReturnAndParameterAnnotationsFollowPhysicalGenericPositions()
    {
        AssertPair(Context.Create(Method("nativeNonNull").ReturnParameter), NullabilityState.NotNull);
        AssertPair(Context.Create(Method("nativeNullable").ReturnParameter), NullabilityState.Nullable);
        AssertPair(Context.Create(Method("nativeEcho").GetParameters()[0]), NullabilityState.NotNull);
        AssertPair(Context.Create(Method("nativeEcho").ReturnParameter), NullabilityState.NotNull);
        var head = Context.Create(Method("collapsedHead").ReturnParameter);
        Assert.That(head.Type, Is.EqualTo(typeof(object)));
        Assert.That(head.GenericTypeArguments, Is.Empty);
        Assert.That(head.ReadState, Is.EqualTo(NullabilityState.Nullable));
        LogicalType(Method("collapsedHead").ReturnParameter.CustomAttributes, Comparable());
    }

    [Test]
    public void ConstructorFieldsAndPropertiesRetainTheirFollowingAnnotations()
    {
        var type = typeof(NativeNrtSlots);
        AssertPair(Context.Create(type.GetConstructors()[0].GetParameters()[0]), NullabilityState.NotNull);
        AssertPair(Context.Create(type.GetField("fieldSlot")!), NullabilityState.NotNull);
        AssertPair(Context.Create(type.GetField("nullableFieldSlot")!), NullabilityState.Nullable);
        AssertPair(Context.Create(type.GetProperty("propertySlot")!), NullabilityState.NotNull);
        AssertPair(Context.Create(type.GetProperty("nullablePropertySlot")!), NullabilityState.Nullable);
        AssertPair(Context.Create(type.GetProperty("propertySlot")!.SetMethod!.GetParameters()[0]),
            NullabilityState.NotNull);
    }

    [Test]
    public void RetainedAndNestedArgumentsKeepTheirOwnPositions()
    {
        var retained = Context.Create(Method("nativeRetained").ReturnParameter);
        var comparable = retained.GenericTypeArguments[0];
        Assert.That(comparable.Type, Is.EqualTo(typeof(IComparable<string>)));
        Assert.That(comparable.ReadState, Is.EqualTo(NullabilityState.Nullable));
        Assert.That(comparable.GenericTypeArguments[0].ReadState, Is.EqualTo(NullabilityState.NotNull));
        Assert.That(retained.GenericTypeArguments[1].ReadState, Is.EqualTo(NullabilityState.NotNull));
        var nested = Context.Create(Method("nativeNested").ReturnParameter);
        AssertPair(nested.GenericTypeArguments[0], NullabilityState.NotNull);
        Assert.That(nested.GenericTypeArguments[1].ReadState, Is.EqualTo(NullabilityState.Nullable));
    }

    [Test]
    public void RewrittenOverridePreservesPhysicalParameterAndReturnPositions()
    {
        var method = typeof(NativeStringNrtExchange).GetMethod("exchange")!;
        foreach (var info in new[] { Context.Create(method.ReturnParameter), Context.Create(method.GetParameters()[0]) })
        {
            Assert.That(info.GenericTypeArguments.Length, Is.EqualTo(3));
            Assert.That(info.GenericTypeArguments[0].Type, Is.EqualTo(typeof(string)));
            Assert.That(info.GenericTypeArguments[0].ReadState, Is.EqualTo(NullabilityState.Nullable));
            Assert.That(info.GenericTypeArguments[1].Type, Is.EqualTo(typeof(IComparable)));
            Assert.That(info.GenericTypeArguments[1].ReadState, Is.EqualTo(NullabilityState.Nullable));
            Assert.That(info.GenericTypeArguments[2].ReadState, Is.EqualTo(NullabilityState.NotNull));
        }
    }

    private static JsonNode Fqn(string name, params JsonNode[] arguments) => arguments.Length == 0
        ? new JsonObject { ["t"] = "fqn", ["name"] = name }
        : new JsonObject { ["t"] = "fqn", ["name"] = name, ["args"] = new JsonArray(arguments) };
    private static JsonNode Nullable(JsonNode type) => new JsonObject { ["t"] = "nullable", ["of"] = type };
    private static JsonNode Text() => Fqn("kotlin.String");
    private static JsonNode Comparable() => Nullable(Fqn("kotlin.Comparable", Nullable(Fqn("kotlin.Any"))));
    private static JsonNode Pair(bool nullableSecond = false) => Fqn("roundtrip.physicalnrtpositions.NrtPair",
        Comparable(), nullableSecond ? Nullable(Text()) : Text());

    private static void Carrier(NullabilityInfo info, IEnumerable<CustomAttributeData> attributes,
        JsonNode expected, string name = "NrtPair")
    {
        Assert.That(info.Type.FullName, Is.EqualTo("roundtrip.physicalnrtpositions." + name + "$star"));
        Assert.That(info.Type.IsInterface, Is.True);
        Assert.That(info.GenericTypeArguments, Is.Empty);
        Assert.That(info.ReadState, Is.EqualTo(NullabilityState.NotNull));
        LogicalType(attributes, expected);
    }

    private static void LogicalType(IEnumerable<CustomAttributeData> attributes, JsonNode expected)
    {
        var metadata = attributes.Single(a => a.AttributeType.FullName ==
            "DotKt.Runtime.CompilerServices.KotlinTypeAttribute");
        Assert.That(metadata.ConstructorArguments[0].Value, Is.EqualTo("bir-json/1"));
        var bytes = ((IEnumerable<CustomAttributeTypedArgument>)metadata.ConstructorArguments[1].Value!)
            .Select(a => (byte)a.Value!).ToArray();
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(bytes), expected), Is.True, expected.ToJsonString());
    }

    private static void Carrier(ParameterInfo parameter, JsonNode expected, string name = "NrtPair") =>
        Carrier(Context.Create(parameter), parameter.CustomAttributes, expected, name);

    [Test]
    public void ErasedResultsAndParametersRetainCompleteLogicalTypes()
    {
        Carrier(Method("collapsedNonNull").ReturnParameter, Pair());
        Carrier(Method("collapsedNullable").ReturnParameter, Pair(true));
        Carrier(Method("echoCollapsed").ReturnParameter, Pair());
        Carrier(Method("echoCollapsed").GetParameters()[0], Pair());
        Carrier(Method("retainedGeneric").ReturnParameter,
            Fqn("roundtrip.physicalnrtpositions.NrtPair", Nullable(Fqn("kotlin.Comparable", Text())), Text()));
        Carrier(Method("nestedCollapsed").ReturnParameter,
            Fqn("roundtrip.physicalnrtpositions.NrtPair", Pair(), Nullable(Text())));
    }

    [Test]
    public void ErasedStorageRetainsCompleteLogicalTypes()
    {
        var type = typeof(NrtSlots);
        Carrier(type.GetConstructors()[0].GetParameters()[0], Pair());
        foreach (var nullable in new[] { false, true })
        {
            var field = type.GetField(nullable ? "nullableFieldSlot" : "fieldSlot")!;
            Carrier(Context.Create(field), field.CustomAttributes, Pair(nullable));
            var property = type.GetProperty(nullable ? "nullablePropertySlot" : "propertySlot")!;
            Carrier(Context.Create(property), property.GetMethod!.ReturnParameter.CustomAttributes, Pair(nullable));
            Carrier(property.SetMethod!.GetParameters()[0], Pair(nullable));
        }
    }

    [Test]
    public void ErasedOverrideRetainsCompleteLogicalTypes()
    {
        var method = typeof(StringNrtExchange).GetMethod("exchange")!;
        foreach (var parameter in new[] { method.ReturnParameter, method.GetParameters()[0] })
            Carrier(parameter, Fqn("roundtrip.physicalnrtpositions.NrtTriple",
                Nullable(Text()), Comparable(), Text()), "NrtTriple");
    }
}
