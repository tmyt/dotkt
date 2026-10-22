using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

public class NativeEnumerationTests
{
    private static void AssertEnumeration<T>(object value, params T[] expected)
    {
        var generic = (IEnumerable<T>)value;
        Assert.That(generic.ToArray(), Is.EqualTo(expected));
        Assert.That(((IEnumerable)value).Cast<object>().ToArray(), Is.EqualTo(expected.Cast<object>().ToArray()));
        Assert.That(generic, Is.SameAs(value));
        Assert.That(NativeEnumerationKt.kotlinMutableEnumerationContains(value, expected[0]), Is.True);
    }

    [Test]
    public void MutableSetTypesLoadAndEnumerateReferenceValueAndNullableElements()
    {
        Assert.DoesNotThrow(() => typeof(NativeEnumerationKt).Assembly.GetTypes());
        AssertEnumeration(NativeEnumerationKt.stringMutableEnumerationSet(), "a", "b");
        AssertEnumeration(NativeEnumerationKt.intMutableEnumerationSet(), 1, 2);
        AssertEnumeration<object>(NativeEnumerationKt.nullableMutableEnumerationSet(), null, 3);
    }

    [Test]
    public void InheritedIteratorsWorkThroughLocalAndReferencedGenericBases()
    {
        AssertEnumeration(NativeEnumerationKt.localInheritedEnumerationSet(), "local");
        AssertEnumeration(NativeEnumerationKt.referencedInheritedEnumerationSet(), "referenced");
    }

    [Test]
    public void AuthoredGetEnumeratorKeepsItsMeaningBesideTheNativeSlots()
    {
        var value = NativeEnumerationKt.namedEnumerationSet();
        Assert.That(NativeEnumerationKt.authoredEnumeratorValue(value), Is.EqualTo(99));
        var authored = value.GetType().GetMethod("GetEnumerator", Type.EmptyTypes)!;
        Assert.That(authored.ReturnType, Is.EqualTo(typeof(int)));
        Assert.That(authored.Invoke(value, null), Is.EqualTo(99));
        AssertEnumeration(value, "named");
    }
}
