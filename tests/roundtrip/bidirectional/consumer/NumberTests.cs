using System;
using System.Linq;
using NUnit.Framework;
using roundtrip.numbers;

public class NumberInteropTests
{
    [Test]
    public void NativeBoxingSignaturesAndComparisonAcceptNumericValues()
    {
        Assert.That(NumbersKt.numberValue(17), Is.EqualTo(17));
        var slot = new NumberSlots(18, null);
        Assert.That(slot.read(), Is.TypeOf<int>().And.EqualTo(18));
        Assert.That(typeof(NumberSlots).GetProperty("value")!.PropertyType, Is.EqualTo(typeof(object)));
        Assert.That(typeof(LibraryNumber).BaseType!.FullName, Is.EqualTo("kotlin.Number"));
        var order = new NumberOrder();
        Assert.That(((IComparable)order).CompareTo(19), Is.EqualTo(19));
        var method = typeof(NumberOrder).GetMethod("CompareTo", new[] { typeof(object) })!;
        Assert.That(method.Invoke(order, new object[] { 20 }), Is.EqualTo(20));
        var bound = typeof(NumberSlots).Assembly.GetTypes().Single(type => type.Namespace == "roundtrip.numbers"
            && type.Name.StartsWith("NumberBound`", StringComparison.Ordinal));
        foreach (var argument in bound.GetGenericArguments())
            Assert.That(argument.GetGenericParameterConstraints().Any(type => type.FullName == "kotlin.Number"), Is.False);
    }
}
