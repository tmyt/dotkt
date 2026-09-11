using System;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using roundtrip.nestedsuspendnullability;

public class NestedSuspendNullabilityTests
{
    private static NullabilityInfo Result(string name) =>
        new NullabilityInfoContext().Create(typeof(NestedSuspendNullabilityKt).GetMethod(name)!.ReturnParameter);

    private static void State(NullabilityInfo info, NullabilityState expected) =>
        Assert.That(info.ReadState, Is.EqualTo(expected));

    [Test]
    public async Task TaskResultsRetainNestedReferenceAndArrayNullability()
    {
        foreach (var name in new[] { "nullableBox", "nonNullBox" })
        {
            var task = Result(name);
            Assert.That(task.Type, Is.EqualTo(typeof(Task<InvariantBox<string>>)));
            State(task, NullabilityState.NotNull);
            var box = task.GenericTypeArguments[0];
            State(box, name == "nullableBox" ? NullabilityState.Nullable : NullabilityState.NotNull);
            State(box.GenericTypeArguments[0], NullabilityState.Nullable);
        }
        var array = Result("nullableArray").GenericTypeArguments[0];
        Assert.That(array.Type, Is.EqualTo(typeof(string[])));
        State(array, NullabilityState.Nullable);
        State(array.ElementType!, NullabilityState.Nullable);
        var nested = Result("nestedArray").GenericTypeArguments[0].GenericTypeArguments[0];
        State(nested, NullabilityState.Nullable);
        State(nested.ElementType!, NullabilityState.Nullable);
        Assert.That((await NestedSuspendNullabilityKt.nonNullBox()).value, Is.Null);
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
        Assert.That(function.Type, Is.EqualTo(typeof(Func<string, string>)));
        State(function, NullabilityState.Nullable);
        State(function.GenericTypeArguments[0], NullabilityState.Nullable);
        State(function.GenericTypeArguments[1], NullabilityState.Nullable);
        Assert.That((await NestedSuspendNullabilityKt.functionResult())!(null!), Is.Null);

        var action = Result("actionResult").GenericTypeArguments[0];
        Assert.That(action.Type, Is.EqualTo(typeof(Action<string>)));
        State(action.GenericTypeArguments[0], NullabilityState.Nullable);
        var receiver = Result("receiverResult").GenericTypeArguments[0];
        Assert.That(receiver.Type, Is.EqualTo(typeof(Func<string, string, string>)));
        State(receiver.GenericTypeArguments[0], NullabilityState.Nullable);
        State(receiver.GenericTypeArguments[1], NullabilityState.NotNull);
        State(receiver.GenericTypeArguments[2], NullabilityState.Nullable);
        var unitFunction = Result("unitFunctionResult").GenericTypeArguments[0];
        Assert.That(unitFunction.Type, Is.EqualTo(typeof(Func<kotlin.Unit>)));
        State(unitFunction.GenericTypeArguments[0], NullabilityState.Nullable);
        var suspendFunction = Result("suspendFunctionResult").GenericTypeArguments[0];
        Assert.That(suspendFunction.Type, Is.EqualTo(typeof(object)));
        State(suspendFunction, NullabilityState.Nullable);
        Assert.That(suspendFunction.GenericTypeArguments, Is.Empty);
        var collapsed = Result("collapsedResult").GenericTypeArguments[0];
        Assert.That(collapsed.GenericTypeArguments[0].Type.IsGenericType, Is.False);
        State(collapsed.GenericTypeArguments[0], NullabilityState.Nullable);
        State(collapsed.GenericTypeArguments[1], NullabilityState.Nullable);
    }

    [Test]
    public void UnitAndAbstractDefaultSlotsUseTheirOwnNullabilityConventions()
    {
        var unit = Result("nestedUnit").GenericTypeArguments[0];
        State(unit, NullabilityState.NotNull);
        unit = unit.GenericTypeArguments[0];
        State(unit, NullabilityState.Nullable);
        State(unit.GenericTypeArguments[0], NullabilityState.Nullable);
        Assert.That(unit.GenericTypeArguments[0].Type, Is.EqualTo(typeof(kotlin.Unit)));
        State(Result("nonNullControl").GenericTypeArguments[0].GenericTypeArguments[0], NullabilityState.NotNull);
        State(Result("ordinaryBox").GenericTypeArguments[0], NullabilityState.Nullable);
        var context = new NullabilityInfoContext();
        var defaultResult = context.Create(typeof(NestedDefault).GetMethod("read")!.ReturnParameter);
        State(defaultResult.GenericTypeArguments[0], NullabilityState.Nullable);
        State(defaultResult.GenericTypeArguments[0].GenericTypeArguments[0], NullabilityState.Nullable);
        var abstractResult = context.Create(typeof(NestedAbstract).GetMethod("read")!.ReturnParameter);
        var array = abstractResult.GenericTypeArguments[0].GenericTypeArguments[0];
        State(array, NullabilityState.Nullable);
        State(array.ElementType!, NullabilityState.Nullable);
    }
}
