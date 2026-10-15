using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using roundtrip.lateinitstorage;

public class LateinitPropertyClrTests
{
    [Test]
    public void TypedPropertiesUsePrivateObjectStorageAndObserveCSharpWrites()
    {
        var type = typeof(LateinitBox<int, int>);
        var property = type.GetProperty("value")!;
        Assert.That(property.PropertyType, Is.EqualTo(typeof(int)));
        Assert.That(property.GetMethod!.ReturnType, Is.EqualTo(typeof(int)));
        Assert.That(property.SetMethod!.GetParameters().Single().ParameterType, Is.EqualTo(typeof(int)));
        Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
        var storage = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).Single();
        Assert.That(storage.IsPrivate, Is.True);
        Assert.That(storage.FieldType, Is.EqualTo(typeof(object)));
        var first = new LateinitBox<int, int>();
        var second = new LateinitBox<int, int>();
        Assert.That(first.initialized(), Is.False);
        Assert.That(storage.GetValue(first), Is.Null);
        var error = Assert.Catch<Exception>(() => { _ = first.value; });
        Assert.That(error!.GetType().FullName, Is.EqualTo("kotlin.UninitializedPropertyAccessException"));
        second.value = 0;
        Assert.That(first.initialized(), Is.False);
        Assert.That(second.initialized(), Is.True);
        Assert.That(second.value, Is.Zero);
        Assert.That(storage.GetValue(second), Is.EqualTo(0));
        var text = new LateinitBox<string, string>();
        Assert.That(text.initialized(), Is.False);
        text.value = "";
        Assert.That(text.initialized(), Is.True);
        Assert.That(text.value, Is.Empty);
    }
}
