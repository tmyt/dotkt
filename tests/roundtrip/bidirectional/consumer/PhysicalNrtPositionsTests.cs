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
    private static JsonElement AssertCarrier(ParameterInfo parameter, string owner, int arity)
    {
        var info = Context.Create(parameter);
        Assert.That(info.ReadState, Is.EqualTo(NullabilityState.NotNull));
        Assert.That(info.Type.IsInterface, Is.True);
        Assert.That(info.Type.IsGenericType, Is.False);
        Assert.That(info.GenericTypeArguments, Is.Empty);
        var source = KotlinTypeMetadata.Read(parameter.GetCustomAttributesData());
        KotlinTypeMetadata.Named(source, owner);
        Assert.That(source.GetProperty("args").GetArrayLength(), Is.EqualTo(arity));
        return source;
    }
    private static void AssertSourcePair(JsonElement source, bool nullableSecond)
    {
        KotlinTypeMetadata.Named(source, "roundtrip.physicalnrtpositions.NrtPair");
        var arguments = source.GetProperty("args");
        Assert.That(arguments.GetArrayLength(), Is.EqualTo(2));
        var comparable = KotlinTypeMetadata.Nullable(arguments[0]);
        KotlinTypeMetadata.Named(comparable, "kotlin.Comparable");
        KotlinTypeMetadata.Named(KotlinTypeMetadata.Nullable(comparable.GetProperty("args")[0]), "kotlin.Any");
        KotlinTypeMetadata.Named(nullableSecond ? KotlinTypeMetadata.Nullable(arguments[1]) : arguments[1], "kotlin.String");
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
        AssertSourcePair(AssertCarrier(Method("collapsedNonNull").ReturnParameter,
            "roundtrip.physicalnrtpositions.NrtPair", 2), false);
        AssertSourcePair(AssertCarrier(Method("collapsedNullable").ReturnParameter,
            "roundtrip.physicalnrtpositions.NrtPair", 2), true);
        var source = AssertCarrier(Method("echoCollapsed").GetParameters()[0],
            "roundtrip.physicalnrtpositions.NrtPair", 2);
        Assert.That(source.GetProperty("args")[0].GetProperty("t").GetString(), Is.EqualTo("nullable"));
        Assert.That(source.GetProperty("args")[1].GetProperty("name").GetString(), Is.EqualTo("kotlin.String"));
        AssertSourcePair(AssertCarrier(Method("echoCollapsed").ReturnParameter,
            "roundtrip.physicalnrtpositions.NrtPair", 2), false);
        var head = Context.Create(Method("collapsedHead").ReturnParameter);
        Assert.That(head.Type, Is.EqualTo(typeof(object)));
        Assert.That(head.ReadState, Is.EqualTo(NullabilityState.Nullable));
    }

    [Test]
    public void ConstructorFieldsAndPropertiesRetainTheirFollowingAnnotations()
    {
        var type = typeof(NrtSlots);
        AssertSourcePair(AssertCarrier(type.GetConstructors()[0].GetParameters()[0],
            "roundtrip.physicalnrtpositions.NrtPair", 2), false);
        AssertPair(Context.Create(type.GetField("fieldSlot")!), NullabilityState.NotNull);
        AssertPair(Context.Create(type.GetField("nullableFieldSlot")!), NullabilityState.Nullable);
        foreach (var name in new[] { "propertySlot", "nullablePropertySlot" })
        {
            var property = type.GetProperty(name)!;
            var info = Context.Create(property);
            StateCarrier(info);
            foreach (var parameter in new[] { property.GetMethod!.ReturnParameter, property.SetMethod!.GetParameters()[0] })
                AssertSourcePair(AssertCarrier(parameter, "roundtrip.physicalnrtpositions.NrtPair", 2),
                    name == "nullablePropertySlot");
        }
    }

    [Test]
    public void RetainedAndNestedArgumentsKeepTheirOwnPositions()
    {
        var sourcePair = AssertCarrier(Method("retainedGeneric").ReturnParameter,
            "roundtrip.physicalnrtpositions.NrtPair", 2);
        Assert.That(sourcePair.GetProperty("name").GetString(), Is.EqualTo("roundtrip.physicalnrtpositions.NrtPair"));
        var sourceComparable = sourcePair.GetProperty("args")[0];
        Assert.That(sourceComparable.GetProperty("t").GetString(), Is.EqualTo("nullable"));
        sourceComparable = sourceComparable.GetProperty("of");
        Assert.That(sourceComparable.GetProperty("name").GetString(), Is.EqualTo("kotlin.Comparable"));
        Assert.That(sourceComparable.GetProperty("args").GetArrayLength(), Is.EqualTo(1));
        var sourceString = sourceComparable.GetProperty("args")[0];
        Assert.That(sourceString.GetProperty("t").GetString(), Is.EqualTo("fqn"));
        Assert.That(sourceString.GetProperty("name").GetString(), Is.EqualTo("kotlin.String"));
        KotlinTypeMetadata.Named(sourcePair.GetProperty("args")[1], "kotlin.String");
        var nested = AssertCarrier(Method("nestedCollapsed").ReturnParameter,
            "roundtrip.physicalnrtpositions.NrtPair", 2);
        AssertSourcePair(nested.GetProperty("args")[0], false);
        KotlinTypeMetadata.Named(KotlinTypeMetadata.Nullable(nested.GetProperty("args")[1]), "kotlin.String");
    }

    [Test]
    public void RewrittenOverridePreservesPhysicalParameterAndReturnPositions()
    {
        var method = typeof(StringNrtExchange).GetMethod("exchange")!;
        var parameter = AssertCarrier(method.GetParameters()[0], "roundtrip.physicalnrtpositions.NrtTriple", 3);
        Assert.That(parameter.GetProperty("args")[0].GetProperty("t").GetString(), Is.EqualTo("nullable"));
        Assert.That(parameter.GetProperty("args")[1].GetProperty("t").GetString(), Is.EqualTo("nullable"));
        Assert.That(parameter.GetProperty("args")[2].GetProperty("name").GetString(), Is.EqualTo("kotlin.String"));
        var result = AssertCarrier(method.ReturnParameter, "roundtrip.physicalnrtpositions.NrtTriple", 3);
        Assert.That(result.GetRawText(), Is.EqualTo(parameter.GetRawText()));
        KotlinTypeMetadata.Named(KotlinTypeMetadata.Nullable(result.GetProperty("args")[0]), "kotlin.String");
        var comparable = KotlinTypeMetadata.Nullable(result.GetProperty("args")[1]);
        KotlinTypeMetadata.Named(comparable, "kotlin.Comparable");
        KotlinTypeMetadata.Named(KotlinTypeMetadata.Nullable(comparable.GetProperty("args")[0]), "kotlin.Any");
    }

    private static void StateCarrier(NullabilityInfo info)
    {
        Assert.That(info.ReadState, Is.EqualTo(NullabilityState.NotNull));
        Assert.That(info.Type.IsInterface, Is.True);
        Assert.That(info.Type.IsGenericType, Is.False);
        Assert.That(info.GenericTypeArguments, Is.Empty);
    }
}
