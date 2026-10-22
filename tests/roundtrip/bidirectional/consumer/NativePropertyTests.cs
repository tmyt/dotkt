using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using BidirectionalInterop;
using NUnit.Framework;

public class NativePropertyTests
{
    [Test]
    public void NativeOverridesSupportOrdinaryReflectionAndCSharpDispatch()
    {
        var instance = new ExportedNativeProperties();
        NativePropertyBase native = instance;
        var item = typeof(ExportedNativeProperties).GetProperty("Item")!;
        Assert.That(item.DeclaringType, Is.EqualTo(typeof(ExportedNativeProperties)));
        Assert.That(item.PropertyType, Is.EqualTo(typeof(IComparable)));
        Assert.That(item.GetMethod!.ReturnType, Is.EqualTo(typeof(IComparable)));
        Assert.That(item.SetMethod!.GetParameters().Single().ParameterType, Is.EqualTo(typeof(IComparable)));
        IComparable first = "reflection";
        item.SetValue(instance, first);
        Assert.That(instance.Item, Is.SameAs(first));
        Assert.That(native.Item, Is.SameAs(first));
        IComparable second = "native";
        native.Item = second;
        Assert.That(item.GetValue(instance), Is.SameAs(second));
        Assert.That(instance.Caption, Is.EqualTo("override"));
        Assert.That(typeof(ExportedNativeProperties).GetProperty("Caption")!.GetValue(instance), Is.EqualTo("override"));
        var restricted = typeof(ExportedNativeProperties).GetProperty("ProtectedValue",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.That(restricted.DeclaringType, Is.EqualTo(typeof(ExportedNativeProperties)));
        Assert.That(restricted.GetMethod!.IsFamily, Is.True);
        Assert.That(native.ReadProtected(), Is.EqualTo(42));
        NativePropertiesKt.exportedStaticProperty = 31;
        Assert.That(typeof(NativePropertiesKt).GetProperty("exportedStaticProperty")!.GetValue(null), Is.EqualTo(31));
    }

    [Test]
    public void PropertySignaturesMatchTheirAccessorsInstanceConvention()
    {
        using var pe = new PEReader(File.OpenRead(typeof(ExportedNativeProperties).Assembly.Location));
        var metadata = pe.GetMetadataReader();
        int instanceCount = 0, staticCount = 0;
        foreach (var handle in metadata.PropertyDefinitions)
        {
            var property = metadata.GetPropertyDefinition(handle);
            var signature = metadata.GetBlobReader(property.Signature);
            var isInstance = signature.ReadSignatureHeader().IsInstance;
            var accessors = property.GetAccessors();
            foreach (var accessor in new[] { accessors.Getter, accessors.Setter }.Where(method => !method.IsNil))
                Assert.That(isInstance, Is.EqualTo(
                    (metadata.GetMethodDefinition(accessor).Attributes & MethodAttributes.Static) == 0),
                    metadata.GetString(property.Name));
            if (isInstance) instanceCount++; else staticCount++;
        }
        Assert.That(instanceCount, Is.GreaterThan(0));
        Assert.That(staticCount, Is.GreaterThan(0));
    }
}
