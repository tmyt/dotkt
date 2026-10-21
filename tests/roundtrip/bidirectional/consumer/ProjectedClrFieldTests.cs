using System;
using System.Reflection;
using NUnit.Framework;
using fieldprojection;

public class ProjectedClrFieldAbiTests
{
    [Test]
    public void ProjectedStorageKeepsItsRealTypedPublicClrField()
    {
        foreach (var (instance, expectedType, replacement) in new[] {
            (ProjectedClrFieldSurfaceKt.stringFieldBox(), typeof(string), (object)"changed"),
            (ProjectedClrFieldSurfaceKt.intFieldBox(), typeof(int), (object)43),
            (ProjectedClrFieldSurfaceKt.nullableFieldBox(), typeof(object), (object)44),
        }) {
            var field = instance.GetType().GetField("value", BindingFlags.Instance | BindingFlags.Public)!;
            Assert.That(field, Is.Not.Null);
            Assert.That(field.FieldType, Is.EqualTo(expectedType));
            Assert.That(field.IsInitOnly, Is.False);
            Assert.That(instance.GetType().GetProperty("value", BindingFlags.Instance | BindingFlags.Public), Is.Null);
            field.SetValue(instance, replacement);
            Assert.That(field.GetValue(instance), Is.EqualTo(replacement));
        }
    }
}
