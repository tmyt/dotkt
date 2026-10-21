using System;
using System.Reflection;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using roundtrip.physicalnrtpositions;

public class PhysicalNrtPositionsTests
{
    private static readonly NullabilityInfoContext Context = new();
    private static MethodInfo Method(string name) => typeof(PhysicalNrtPositionsKt).GetMethod(name)!;
    private static JsonDocument SourceType(ParameterInfo parameter)
    {
        var attribute = parameter.GetCustomAttributesData().Single(value =>
            value.AttributeType.FullName == "DotKt.Runtime.CompilerServices.KotlinTypeAttribute");
        Assert.That(attribute.ConstructorArguments[0].Value, Is.EqualTo("bir-json/1"));
        var bytes = ((System.Collections.Generic.IEnumerable<CustomAttributeTypedArgument>)
            attribute.ConstructorArguments[1].Value!).Select(value => (byte)value.Value!).ToArray();
        return JsonDocument.Parse(bytes);
    }

    private static JsonElement AssertCarrier(ParameterInfo parameter, string owner, int arity)
    {
        var info = Context.Create(parameter);
        Assert.That(info.ReadState, Is.EqualTo(NullabilityState.NotNull));
        Assert.That(info.Type.IsInterface, Is.True);
        Assert.That(info.Type.IsGenericType, Is.False);
        Assert.That(info.GenericTypeArguments, Is.Empty);
        using var source = SourceType(parameter);
        Assert.That(source.RootElement.GetProperty("name").GetString(), Is.EqualTo(owner));
        Assert.That(source.RootElement.GetProperty("args").GetArrayLength(), Is.EqualTo(arity));
        return source.RootElement.Clone();
    }
    private static void AssertPair(NullabilityInfo info, NullabilityState second)
    {
        Assert.That(info.ReadState, Is.EqualTo(NullabilityState.NotNull));
        Assert.That(info.GenericTypeArguments.Length, Is.EqualTo(4));
        Assert.That(info.GenericTypeArguments[0].Type, Is.EqualTo(typeof(object)));
        Assert.That(info.GenericTypeArguments[0].ReadState, Is.EqualTo(NullabilityState.Nullable));
        Assert.That(info.GenericTypeArguments[1].ReadState, Is.EqualTo(second));
        for (var index = 0; index < 2; index++)
        {
            Assert.That(info.GenericTypeArguments[index + 2].Type,
                Is.EqualTo(info.GenericTypeArguments[index].Type));
            Assert.That(info.GenericTypeArguments[index + 2].ReadState,
                Is.EqualTo(info.GenericTypeArguments[index].ReadState));
        }
    }

    [Test]
    public void ReturnAndParameterAnnotationsFollowPhysicalGenericPositions()
    {
        AssertPair(Context.Create(Method("collapsedNonNull").ReturnParameter), NullabilityState.NotNull);
        AssertPair(Context.Create(Method("collapsedNullable").ReturnParameter), NullabilityState.Nullable);
        var source = AssertCarrier(Method("echoCollapsed").GetParameters()[0],
            "roundtrip.physicalnrtpositions.NrtPair", 2);
        Assert.That(source.GetProperty("args")[0].GetProperty("t").GetString(), Is.EqualTo("nullable"));
        Assert.That(source.GetProperty("args")[1].GetProperty("name").GetString(), Is.EqualTo("kotlin.String"));
        AssertPair(Context.Create(Method("echoCollapsed").ReturnParameter), NullabilityState.NotNull);
        var head = Context.Create(Method("collapsedHead").ReturnParameter);
        Assert.That(head.Type, Is.EqualTo(typeof(object)));
        Assert.That(head.ReadState, Is.EqualTo(NullabilityState.Nullable));
    }

    [Test]
    public void ConstructorFieldsAndPropertiesRetainTheirFollowingAnnotations()
    {
        var type = typeof(NrtSlots);
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
        var retained = Context.Create(Method("retainedGeneric").ReturnParameter);
        var comparable = retained.GenericTypeArguments[0];
        Assert.That(comparable.Type, Is.EqualTo(typeof(object)));
        Assert.That(comparable.ReadState, Is.EqualTo(NullabilityState.Nullable));
        Assert.That(comparable.GenericTypeArguments, Is.Empty);
        using var source = SourceType(Method("retainedGeneric").ReturnParameter);
        var sourcePair = source.RootElement;
        Assert.That(sourcePair.GetProperty("name").GetString(), Is.EqualTo("roundtrip.physicalnrtpositions.NrtPair"));
        var sourceComparable = sourcePair.GetProperty("args")[0];
        Assert.That(sourceComparable.GetProperty("t").GetString(), Is.EqualTo("nullable"));
        sourceComparable = sourceComparable.GetProperty("of");
        Assert.That(sourceComparable.GetProperty("name").GetString(), Is.EqualTo("kotlin.Comparable"));
        Assert.That(sourceComparable.GetProperty("args").GetArrayLength(), Is.EqualTo(1));
        var sourceString = sourceComparable.GetProperty("args")[0];
        Assert.That(sourceString.GetProperty("t").GetString(), Is.EqualTo("fqn"));
        Assert.That(sourceString.GetProperty("name").GetString(), Is.EqualTo("kotlin.String"));
        Assert.That(retained.GenericTypeArguments[1].ReadState, Is.EqualTo(NullabilityState.NotNull));
        var nested = Context.Create(Method("nestedCollapsed").ReturnParameter);
        AssertPair(nested.GenericTypeArguments[0], NullabilityState.NotNull);
        Assert.That(nested.GenericTypeArguments[1].ReadState, Is.EqualTo(NullabilityState.Nullable));
    }

    [Test]
    public void RewrittenOverridePreservesPhysicalParameterAndReturnPositions()
    {
        var method = typeof(StringNrtExchange).GetMethod("exchange")!;
        var parameter = AssertCarrier(method.GetParameters()[0], "roundtrip.physicalnrtpositions.NrtTriple", 3);
        Assert.That(parameter.GetProperty("args")[0].GetProperty("t").GetString(), Is.EqualTo("nullable"));
        Assert.That(parameter.GetProperty("args")[1].GetProperty("t").GetString(), Is.EqualTo("nullable"));
        Assert.That(parameter.GetProperty("args")[2].GetProperty("name").GetString(), Is.EqualTo("kotlin.String"));
        var info = Context.Create(method.ReturnParameter);
        Assert.That(info.GenericTypeArguments.Length, Is.EqualTo(6));
        Assert.That(info.GenericTypeArguments[0].Type, Is.EqualTo(typeof(string)));
        Assert.That(info.GenericTypeArguments[0].ReadState, Is.EqualTo(NullabilityState.Nullable));
        Assert.That(info.GenericTypeArguments[1].Type, Is.EqualTo(typeof(object)));
        Assert.That(info.GenericTypeArguments[1].ReadState, Is.EqualTo(NullabilityState.Nullable));
        Assert.That(info.GenericTypeArguments[2].ReadState, Is.EqualTo(NullabilityState.NotNull));
        for (var index = 0; index < 3; index++)
        {
            Assert.That(info.GenericTypeArguments[index + 3].Type,
                Is.EqualTo(info.GenericTypeArguments[index].Type));
            Assert.That(info.GenericTypeArguments[index + 3].ReadState,
                Is.EqualTo(info.GenericTypeArguments[index].ReadState));
        }
    }
}
