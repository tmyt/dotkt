#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using roundtrip.nestedsuspendnullability;

public class NestedSuspendNullabilityTests
{
    private static NullabilityInfo Result(string name) =>
        new NullabilityInfoContext().Create(typeof(NestedSuspendNullabilityKt).GetMethod(name)!.ReturnParameter);

    private static void State(NullabilityInfo info, NullabilityState expected) =>
        Assert.That(info.ReadState, Is.EqualTo(expected));

    private static JsonElement LogicalResult(string name, Type? owner = null) =>
        KotlinTypeMetadata.Read((owner ?? typeof(NestedSuspendNullabilityKt)).GetMethod(name)!
            .GetCustomAttributesData(), "KotlinSuspendResultAttribute");

    private static void Carrier(NullabilityInfo info, NullabilityState state = NullabilityState.NotNull)
    {
        State(info, state);
        Assert.That(info.Type.IsInterface, Is.True);
        Assert.That(info.Type.IsGenericType, Is.False);
        Assert.That(info.GenericTypeArguments, Is.Empty);
    }

    private static byte[] Flags(string name)
    {
        var attribute = typeof(NestedSuspendNullabilityKt).GetMethod(name)!.ReturnParameter
            .GetCustomAttributesData().SingleOrDefault(a => a.AttributeType.Name == "NullableAttribute");
        if (attribute is null) return Array.Empty<byte>();
        var value = attribute.ConstructorArguments[0].Value;
        return value is byte scalar ? new[] { scalar } :
            ((IEnumerable<CustomAttributeTypedArgument>)value!).Select(a => (byte)a.Value!).ToArray();
    }

    [Test]
    public async Task TaskResultsRetainNestedReferenceAndArrayNullability()
    {
        foreach (var name in new[] { "nullableBox", "nonNullBox" })
        {
            var task = Result(name);
            Assert.That(task.Type.GetGenericTypeDefinition(), Is.EqualTo(typeof(Task<>)));
            State(task, NullabilityState.NotNull);
            var box = task.GenericTypeArguments[0];
            Carrier(box, name == "nullableBox" ? NullabilityState.Nullable : NullabilityState.NotNull);
            Assert.That(box.Type.IsAssignableFrom(typeof(InvariantBox<string, string>)), Is.True);
            var logical = LogicalResult(name);
            if (name == "nullableBox") logical = KotlinTypeMetadata.Nullable(logical);
            KotlinTypeMetadata.Named(logical, "roundtrip.nestedsuspendnullability.InvariantBox");
            Assert.That(logical.GetProperty("args").GetArrayLength(), Is.EqualTo(1));
            KotlinTypeMetadata.Named(KotlinTypeMetadata.Nullable(logical.GetProperty("args")[0]), "kotlin.String");
        }
        var array = Result("nullableArray").GenericTypeArguments[0];
        Assert.That(array.Type, Is.EqualTo(typeof(string[])));
        State(array, NullabilityState.Nullable);
        State(array.ElementType!, NullabilityState.Nullable);
        Carrier(Result("nestedArray").GenericTypeArguments[0]);
        var nested = KotlinTypeMetadata.Nullable(LogicalResult("nestedArray").GetProperty("args")[0]);
        Assert.That(nested.GetProperty("t").GetString(), Is.EqualTo("array"));
        KotlinTypeMetadata.Named(KotlinTypeMetadata.Nullable(nested.GetProperty("elem")), "kotlin.String");
        Assert.That(((InvariantBox<string, string>)await NestedSuspendNullabilityKt.nonNullBox()).value, Is.Null);
        Assert.That((await NestedSuspendNullabilityKt.nullableArray())![0], Is.Null);
    }

    [Test]
    public async Task ConstructedValuesAndDelegatesFollowTheirPhysicalTypeArguments()
    {
        var segment = Result("valueSegment").GenericTypeArguments[0];
        Assert.That(segment.Type, Is.EqualTo(typeof(ArraySegment<string>)));
        State(segment, NullabilityState.NotNull);
        State(segment.GenericTypeArguments[0], NullabilityState.Nullable);
        Assert.That((await NestedSuspendNullabilityKt.valueSegment())[0], Is.Null);

        var function = Result("functionResult").GenericTypeArguments[0];
        Assert.That(function.Type, Is.EqualTo(typeof(Func<object, object>)));
        State(function, NullabilityState.Nullable);
        State(function.GenericTypeArguments[0], NullabilityState.Nullable);
        State(function.GenericTypeArguments[1], NullabilityState.Nullable);
        Assert.That((await NestedSuspendNullabilityKt.functionResult())!(null!), Is.Null);

        var action = Result("actionResult").GenericTypeArguments[0];
        Assert.That(action.Type, Is.EqualTo(typeof(Func<object, object>)));
        State(action.GenericTypeArguments[0], NullabilityState.Nullable);
        State(action.GenericTypeArguments[1], NullabilityState.NotNull);
        Assert.That((await NestedSuspendNullabilityKt.actionResult())!(null!), Is.TypeOf<kotlin.Unit>());
        var receiver = Result("receiverResult").GenericTypeArguments[0];
        Assert.That(receiver.Type, Is.EqualTo(typeof(Func<object, object, object>)));
        State(receiver.GenericTypeArguments[0], NullabilityState.Nullable);
        State(receiver.GenericTypeArguments[1], NullabilityState.NotNull);
        State(receiver.GenericTypeArguments[2], NullabilityState.Nullable);
        var unitFunction = Result("unitFunctionResult").GenericTypeArguments[0];
        Assert.That(unitFunction.Type, Is.EqualTo(typeof(Func<object>)));
        State(unitFunction.GenericTypeArguments[0], NullabilityState.Nullable);
        Assert.That((await NestedSuspendNullabilityKt.unitFunctionResult())!(), Is.Null);
        var suspendFunction = Result("suspendFunctionResult").GenericTypeArguments[0];
        Assert.That(suspendFunction.Type, Is.EqualTo(typeof(object)));
        State(suspendFunction, NullabilityState.Nullable);
        Assert.That(suspendFunction.GenericTypeArguments, Is.Empty);
        var collapsed = Result("collapsedResult").GenericTypeArguments[0];
        Carrier(collapsed);
        var collapsedSource = LogicalResult("collapsedResult");
        KotlinTypeMetadata.Named(collapsedSource, "roundtrip.nestedsuspendnullability.TwoSlots");
        var pairSource = KotlinTypeMetadata.Nullable(collapsedSource.GetProperty("args")[0]);
        KotlinTypeMetadata.Named(pairSource, "kotlin.Pair");
        Assert.That(pairSource.GetProperty("args").GetArrayLength(), Is.EqualTo(2));
        foreach (var argument in pairSource.GetProperty("args").EnumerateArray())
            KotlinTypeMetadata.Named(argument, "kotlin.String");
        KotlinTypeMetadata.Named(KotlinTypeMetadata.Nullable(collapsedSource.GetProperty("args")[1]), "kotlin.String");
        foreach (var name in new[] { "lateCollapsedResult", "starComparableResult", "enumResult" })
        {
            var pair = Result(name).GenericTypeArguments[0];
            Carrier(pair);
            var source = LogicalResult(name);
            KotlinTypeMetadata.Named(source, "roundtrip.nestedsuspendnullability.TwoSlots");
            Assert.That(source.GetProperty("args").GetArrayLength(), Is.EqualTo(2));
            var first = KotlinTypeMetadata.Nullable(source.GetProperty("args")[0]);
            KotlinTypeMetadata.Named(first, name == "enumResult" ? "kotlin.Enum" : "kotlin.Comparable");
            if (name == "lateCollapsedResult")
                KotlinTypeMetadata.Named(KotlinTypeMetadata.Nullable(first.GetProperty("args")[0]), "kotlin.Any");
            else Assert.That(first.GetProperty("args")[0].GetProperty("t").GetString(), Is.EqualTo("star"));
            KotlinTypeMetadata.Named(source.GetProperty("args")[1], "kotlin.String");
            Assert.That(Flags(name), Is.Empty);
        }
        Carrier(Result("lateCollapsedNullableResult").GenericTypeArguments[0]);
        KotlinTypeMetadata.Named(KotlinTypeMetadata.Nullable(
            LogicalResult("lateCollapsedNullableResult").GetProperty("args")[1]), "kotlin.String");
        Assert.That(Flags("lateCollapsedNullableResult"), Is.Empty);
        Carrier(Result("primitiveResult").GenericTypeArguments[0]);
        var primitive = LogicalResult("primitiveResult");
        KotlinTypeMetadata.Named(primitive.GetProperty("args")[0], "kotlin.Int");
        KotlinTypeMetadata.Named(KotlinTypeMetadata.Nullable(primitive.GetProperty("args")[1]), "kotlin.String");
    }

    [Test]
    public void UnitAndAbstractDefaultSlotsUseTheirOwnNullabilityConventions()
    {
        var unit = Result("nestedUnit").GenericTypeArguments[0];
        Carrier(unit);
        var unitSource = LogicalResult("nestedUnit");
        KotlinTypeMetadata.Named(unitSource, "roundtrip.nestedsuspendnullability.InvariantBox");
        var inner = KotlinTypeMetadata.Nullable(unitSource.GetProperty("args")[0]);
        KotlinTypeMetadata.Named(inner, "roundtrip.nestedsuspendnullability.InvariantBox");
        KotlinTypeMetadata.Named(KotlinTypeMetadata.Nullable(inner.GetProperty("args")[0]), "kotlin.Unit");
        Carrier(Result("nonNullControl").GenericTypeArguments[0]);
        KotlinTypeMetadata.Named(LogicalResult("nonNullControl").GetProperty("args")[0], "kotlin.String");
        Carrier(Result("ordinaryBox"));
        var ordinarySource = KotlinTypeMetadata.Read(typeof(NestedSuspendNullabilityKt).GetMethod("ordinaryBox")!
            .ReturnParameter.GetCustomAttributesData());
        KotlinTypeMetadata.Named(KotlinTypeMetadata.Nullable(ordinarySource.GetProperty("args")[0]), "kotlin.String");
        Assert.That(Flags("ordinaryFunction"), Is.EqualTo(new byte[] { 2 }));
        Assert.That(Flags("ordinaryUnitBox"), Is.EqualTo(new byte[] { 2 }));
        var context = new NullabilityInfoContext();
        var defaultResult = context.Create(typeof(NestedDefault).GetMethod("read")!.ReturnParameter);
        Carrier(defaultResult.GenericTypeArguments[0], NullabilityState.Nullable);
        var defaultSource = KotlinTypeMetadata.Nullable(LogicalResult("read", typeof(NestedDefault)));
        KotlinTypeMetadata.Named(KotlinTypeMetadata.Nullable(defaultSource.GetProperty("args")[0]), "kotlin.String");
        var abstractResult = context.Create(typeof(NestedAbstract).GetMethod("read")!.ReturnParameter);
        Carrier(abstractResult.GenericTypeArguments[0]);
        var array = KotlinTypeMetadata.Nullable(LogicalResult("read", typeof(NestedAbstract)).GetProperty("args")[0]);
        Assert.That(array.GetProperty("t").GetString(), Is.EqualTo("array"));
        KotlinTypeMetadata.Named(KotlinTypeMetadata.Nullable(array.GetProperty("elem")), "kotlin.String");
    }
}
