using NUnit.Framework;
using roundtrip.genericvalueinterop;
using roundtrip.parameterslots;

public class OrdinaryParameterSlotAbiTests
{
    [Test]
    public void OrdinaryViewsAndManagedReferencesHaveSeparateContracts()
    {
        var ordinary = typeof(OrdinaryParameterSlotsKt).GetMethod("allocateSlot")!.GetParameters()[0].ParameterType;
        Assert.That(ordinary.IsInterface, Is.True);
        Assert.That(ordinary.IsGenericType, Is.False);
        var parameters = typeof(OrdinaryParameterSlotsKt).GetMethod("takeRef")!.GetParameters();
        Assert.That(parameters[0].ParameterType.IsByRef, Is.True);
        Assert.That(parameters[0].ParameterType.GetElementType(), Is.EqualTo(typeof(Box<string, string>)));
        Assert.That(parameters[1].ParameterType.IsInterface, Is.True);
        Assert.That(parameters[1].ParameterType.IsAssignableFrom(typeof(Box<string, string>)), Is.True);
        var property = typeof(PropertyOwner).GetProperty("box")!;
        Assert.That(property.PropertyType, Is.EqualTo(typeof(Box<string, string>)));
        Assert.That(property.GetMethod!.ReturnType, Is.EqualTo(property.PropertyType));
        Assert.That(property.SetMethod!.GetParameters()[0].ParameterType, Is.EqualTo(property.PropertyType));
    }
}
