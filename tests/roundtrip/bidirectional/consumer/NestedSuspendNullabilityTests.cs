using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NUnit.Framework;
using roundtrip.nestedsuspendnullability;

public class NestedSuspendNullabilityTests
{
    private static NullabilityInfo Result(string name) =>
        new NullabilityInfoContext().Create(typeof(NestedSuspendNullabilityKt).GetMethod(name)!.ReturnParameter);

    private static void State(NullabilityInfo info, NullabilityState expected) =>
        Assert.That(info.ReadState, Is.EqualTo(expected));

    private static JsonNode Fqn(string name, params JsonNode[] args)
    {
        var result = new JsonObject { ["t"] = "fqn", ["name"] = name };
        if (args.Length != 0) result["args"] = new JsonArray(args);
        return result;
    }

    private static JsonNode Nullable(JsonNode type) => new JsonObject { ["t"] = "nullable", ["of"] = type };
    private static JsonNode ArrayOf(JsonNode type) => new JsonObject { ["t"] = "array", ["elem"] = type };
    private static JsonNode Box(JsonNode type) => Fqn("roundtrip.nestedsuspendnullability.InvariantBox", type);
    private static JsonNode StringType() => Fqn("kotlin.String");

    private static void LogicalResult(MethodInfo method, JsonNode expected, bool suspend = true)
    {
        var attributes = suspend ? method.GetCustomAttributesData() : method.ReturnParameter.GetCustomAttributesData();
        var attribute = attributes.Single(a => a.AttributeType.FullName ==
            "DotKt.Runtime.CompilerServices." + (suspend ? "KotlinSuspendResultAttribute" : "KotlinTypeAttribute"));
        Assert.That(attribute.ConstructorArguments[0].Value, Is.EqualTo("bir-json/1"));
        var bytes = ((IEnumerable<CustomAttributeTypedArgument>)attribute.ConstructorArguments[1].Value!)
            .Select(a => (byte)a.Value!).ToArray();
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(bytes), expected), Is.True, method.Name);
    }

    private static void LogicalResult(string name, JsonNode expected, bool suspend = true) =>
        LogicalResult(typeof(NestedSuspendNullabilityKt).GetMethod(name)!, expected, suspend);

    private static void Carrier(NullabilityInfo info, string name, NullabilityState state)
    {
        Assert.That(info.Type.FullName, Is.EqualTo("roundtrip.nestedsuspendnullability." + name + "$star"));
        Assert.That(info.Type.IsInterface, Is.True);
        Assert.That(info.GenericTypeArguments, Is.Empty);
        State(info, state);
    }

    private static byte[] Flags(string name)
    {
        var attribute = typeof(NestedSuspendNullabilityKt).GetMethod(name)!.ReturnParameter
            .GetCustomAttributesData().Single(a => a.AttributeType.Name == "NullableAttribute");
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
            Carrier(box, "InvariantBox", name == "nullableBox" ? NullabilityState.Nullable : NullabilityState.NotNull);
            var logicalBox = Box(Nullable(StringType()));
            LogicalResult(name, name == "nullableBox" ? Nullable(logicalBox) : logicalBox);
        }
        var array = Result("nullableArray").GenericTypeArguments[0];
        Assert.That(array.Type, Is.EqualTo(typeof(string[])));
        State(array, NullabilityState.Nullable);
        State(array.ElementType!, NullabilityState.Nullable);
        LogicalResult("nullableArray", Nullable(ArrayOf(Nullable(StringType()))));
        Carrier(Result("nestedArray").GenericTypeArguments[0], "InvariantBox", NullabilityState.NotNull);
        LogicalResult("nestedArray", Box(Nullable(ArrayOf(Nullable(StringType())))));
        Assert.That(((InvariantBox<string>)(await NestedSuspendNullabilityKt.nonNullBox())).value, Is.Null);
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
        foreach (var name in new[] { "collapsedResult", "lateCollapsedResult", "lateCollapsedNullableResult",
            "starComparableResult", "enumResult", "primitiveResult" })
        {
            State(Result(name), NullabilityState.NotNull);
            Carrier(Result(name).GenericTypeArguments[0], "TwoSlots", NullabilityState.NotNull);
        }
        LogicalResult("collapsedResult", Fqn("roundtrip.nestedsuspendnullability.TwoSlots",
            Nullable(Fqn("kotlin.Pair", StringType(), StringType())), Nullable(StringType())));
        foreach (var name in new[] { "lateCollapsedResult", "lateCollapsedNullableResult", "starComparableResult", "enumResult" })
        {
            var argument = name == "starComparableResult" || name == "enumResult"
                ? new JsonObject { ["t"] = "star" } : Nullable(Fqn("kotlin.Any"));
            LogicalResult(name, Fqn("roundtrip.nestedsuspendnullability.TwoSlots",
                Nullable(Fqn(name == "enumResult" ? "kotlin.Enum" : "kotlin.Comparable", argument)),
                name == "lateCollapsedNullableResult" ? Nullable(StringType()) : StringType()));
        }
        LogicalResult("primitiveResult", Fqn("roundtrip.nestedsuspendnullability.TwoSlots",
            Fqn("kotlin.Int"), Nullable(StringType())));

        // Keep a physical nested-slot control: these CLR tuples are not erased.
        foreach (var name in new[] { "nativeComparableSlots", "nativeNullableSlots" })
        {
            var tuple = Result(name).GenericTypeArguments[0];
            Assert.That(tuple.Type.GetGenericTypeDefinition(), Is.EqualTo(typeof(Tuple<,>)));
            State(tuple, NullabilityState.NotNull);
            Assert.That(tuple.GenericTypeArguments[0].Type, Is.EqualTo(typeof(IComparable)));
            State(tuple.GenericTypeArguments[0], NullabilityState.Nullable);
            State(tuple.GenericTypeArguments[1], name == "nativeNullableSlots"
                ? NullabilityState.Nullable : NullabilityState.NotNull);
            Assert.That(Flags(name), Is.EqualTo(name == "nativeNullableSlots"
                ? new byte[] { 1, 1, 2, 2 } : new byte[] { 1, 1, 2, 1 }));
        }
        Assert.That((await NestedSuspendNullabilityKt.nativeComparableSlots()).Item2, Is.EqualTo("value"));
        Assert.That((await NestedSuspendNullabilityKt.nativeNullableSlots()).Item2, Is.Null);

        // A nested existential cannot be substituted into an invariant foreign
        // construction. Its object slot retains the exact runtime tuple instead.
        foreach (var name in new[] { "nativeStarSlots", "nativeEnumSlots" })
        {
            var result = Result(name).GenericTypeArguments[0];
            Assert.That(result.Type, Is.EqualTo(typeof(object)));
            State(result, NullabilityState.NotNull);
            Assert.That(result.GenericTypeArguments, Is.Empty);
            LogicalResult(name, Fqn("System.Tuple`2",
                Nullable(Fqn(name == "nativeEnumSlots" ? "kotlin.Enum" : "kotlin.Comparable",
                    new JsonObject { ["t"] = "star" })), StringType()));
        }
        foreach (var tuple in new[] { await NestedSuspendNullabilityKt.nativeStarSlots(),
            await NestedSuspendNullabilityKt.nativeEnumSlots() })
        {
            Assert.That(tuple.GetType().GetGenericTypeDefinition(), Is.EqualTo(typeof(Tuple<,>)));
            Assert.That(tuple.GetType().GetProperty("Item1")!.GetValue(tuple), Is.Null);
            Assert.That(tuple.GetType().GetProperty("Item2")!.GetValue(tuple), Is.EqualTo("value"));
        }
    }

    [Test]
    public void UnitAndAbstractDefaultSlotsUseTheirOwnNullabilityConventions()
    {
        Carrier(Result("nestedUnit").GenericTypeArguments[0], "InvariantBox", NullabilityState.NotNull);
        LogicalResult("nestedUnit", Box(Nullable(Box(Nullable(Fqn("kotlin.Unit"))))));
        Carrier(Result("nonNullControl").GenericTypeArguments[0], "InvariantBox", NullabilityState.NotNull);
        LogicalResult("nonNullControl", Box(StringType()));
        Carrier(Result("ordinaryBox"), "InvariantBox", NullabilityState.NotNull);
        LogicalResult("ordinaryBox", Box(Nullable(StringType())), suspend: false);
        Assert.That(Flags("ordinaryFunction"), Is.EqualTo(new byte[] { 2 }));
        Assert.That(Flags("ordinaryUnitBox"), Is.EqualTo(new byte[] { 2 }));
        Carrier(Result("ordinaryUnitBox"), "InvariantBox", NullabilityState.Nullable);
        LogicalResult("ordinaryUnitBox", Nullable(Box(Nullable(Fqn("kotlin.Unit")))), suspend: false);
        var context = new NullabilityInfoContext();
        var defaultResult = context.Create(typeof(NestedDefault).GetMethod("read")!.ReturnParameter);
        Carrier(defaultResult.GenericTypeArguments[0], "InvariantBox", NullabilityState.Nullable);
        LogicalResult(typeof(NestedDefault).GetMethod("read")!, Nullable(Box(Nullable(StringType()))));
        var abstractResult = context.Create(typeof(NestedAbstract).GetMethod("read")!.ReturnParameter);
        Carrier(abstractResult.GenericTypeArguments[0], "InvariantBox", NullabilityState.NotNull);
        LogicalResult(typeof(NestedAbstract).GetMethod("read")!, Box(Nullable(ArrayOf(Nullable(StringType())))));
    }
}
