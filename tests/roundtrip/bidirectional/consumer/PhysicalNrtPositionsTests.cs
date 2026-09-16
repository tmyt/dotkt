using System;
using System.Reflection;
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
        AssertPair(Context.Create(Method("collapsedNonNull").ReturnParameter), NullabilityState.NotNull);
        AssertPair(Context.Create(Method("collapsedNullable").ReturnParameter), NullabilityState.Nullable);
        AssertPair(Context.Create(Method("echoCollapsed").GetParameters()[0]), NullabilityState.NotNull);
        AssertPair(Context.Create(Method("echoCollapsed").ReturnParameter), NullabilityState.NotNull);
        var head = Context.Create(Method("collapsedHead").ReturnParameter);
        Assert.That(head.Type, Is.EqualTo(typeof(IComparable)));
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
        Assert.That(comparable.Type, Is.EqualTo(typeof(IComparable<string>)));
        Assert.That(comparable.ReadState, Is.EqualTo(NullabilityState.Nullable));
        Assert.That(comparable.GenericTypeArguments[0].ReadState, Is.EqualTo(NullabilityState.NotNull));
        Assert.That(retained.GenericTypeArguments[1].ReadState, Is.EqualTo(NullabilityState.NotNull));
        var nested = Context.Create(Method("nestedCollapsed").ReturnParameter);
        AssertPair(nested.GenericTypeArguments[0], NullabilityState.NotNull);
        Assert.That(nested.GenericTypeArguments[1].ReadState, Is.EqualTo(NullabilityState.Nullable));
    }

    [Test]
    public void RewrittenOverridePreservesPhysicalParameterAndReturnPositions()
    {
        var method = typeof(StringNrtExchange).GetMethod("exchange")!;
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
}
