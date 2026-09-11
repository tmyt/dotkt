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
