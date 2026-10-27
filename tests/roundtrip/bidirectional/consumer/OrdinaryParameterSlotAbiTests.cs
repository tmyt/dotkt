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
        Assert.That(property.PropertyType.IsInterface, Is.True);
        Assert.That(property.PropertyType.IsGenericType, Is.False);
        Assert.That(property.PropertyType.IsAssignableFrom(typeof(Box<string, string>)), Is.True);
        Assert.That(property.GetMethod!.ReturnType, Is.EqualTo(property.PropertyType));
        Assert.That(property.SetMethod!.GetParameters()[0].ParameterType, Is.EqualTo(property.PropertyType));
        foreach (var parameter in new[] { property.GetMethod.ReturnParameter, property.SetMethod.GetParameters()[0] })
        {
            var source = KotlinTypeMetadata.Read(parameter.GetCustomAttributesData());
            KotlinTypeMetadata.Named(source, "roundtrip.genericvalueinterop.Box");
            Assert.That(source.GetProperty("args").GetArrayLength(), Is.EqualTo(1));
            KotlinTypeMetadata.Named(source.GetProperty("args")[0], "kotlin.String");
        }
    }
}
