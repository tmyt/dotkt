using System;
using System.Linq;
using NUnit.Framework;
using roundtrip.staticnested;

public class StaticNestedGenericOwnerClrTests
{
    [Test]
    public void RoslynImportsNestedInheritanceAndMetadataHasCompleteEnclosingFrames()
    {
        Assert.That(StaticNestedGenericOwnerKt.makeStaticClosed().marker(), Is.EqualTo(42));
        Assert.That(StaticNestedGenericOwnerKt.staticPrivateAccess(), Is.True);
        var types = typeof(StaticNestedOwner<,>).Assembly.GetTypes()
            .Where(type => type.Namespace == "roundtrip.staticnested");
        foreach (var type in types.Where(type => type.IsNested))
            Assert.That(type.GetGenericArguments().Length,
                Is.GreaterThanOrEqualTo(type.DeclaringType!.GetGenericArguments().Length), type.FullName);
        var closed = StaticNestedGenericOwnerKt.makeStaticClosed().GetType();
        Assert.That(closed.IsGenericType, Is.False);
        Assert.That(closed.DeclaringType!.IsGenericType, Is.False);
        Assert.That(closed.BaseType!.DeclaringType, Is.EqualTo(closed.DeclaringType));
        var runtimeEntry = typeof(kotlin.collections.ClrMapDefaultsKt).Assembly
            .GetType("kotlin.collections.MutableMap$dotkt_statics", throwOnError: true)!.GetNestedTypes()
            .Single(type => type.IsGenericTypeDefinition && type.Name.Split('`')[0] == "MutableEntry");
        Assert.That(runtimeEntry.IsNested, Is.True);
        Assert.That(runtimeEntry.DeclaringType!.IsGenericType, Is.False);
    }
}
