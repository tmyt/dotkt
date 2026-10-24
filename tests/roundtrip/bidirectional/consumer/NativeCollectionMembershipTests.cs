using System;
using System.Collections.Generic;
using NUnit.Framework;

public class NativeCollectionMembershipTests
{
    [Test]
    public void NativeReadOnlyIndexOfPreservesOverridesAndNonDispatchingDefaults()
    {
        void Check<T>(object value, T needle, T stored, MembershipCallCounter counter)
        {
            var native = (IList<T>)value;
            Assert.That(NativeCollectionMembershipKt.kotlinIndexOf(value, needle), Is.EqualTo(0));
            Assert.That(native.IndexOf(needle), Is.EqualTo(0));
            Assert.That(native.IndexOf(stored), Is.EqualTo(-1));
            Assert.That(NativeCollectionMembershipKt.kotlinIndexOf(value, stored), Is.EqualTo(-1));
            Assert.That(counter.calls(), Is.EqualTo(4));
            Assert.That(native, Is.SameAs(value));
            Assert.That(native.IsReadOnly, Is.True);
            Assert.That(native.Count, Is.EqualTo(1));
            Assert.That(native[0], Is.EqualTo(stored));
            Assert.Throws<NotSupportedException>(() => native.Add(needle));
        }

        var strings = new MembershipCallCounter();
        Check(NativeCollectionMembershipKt.stringIndexList(strings), "needle", "value", strings);
        var integers = new MembershipCallCounter();
        Check(NativeCollectionMembershipKt.intIndexList(integers), 42, 7, integers);
        var nullable = new MembershipCallCounter();
        Check<int?>(NativeCollectionMembershipKt.nullableIndexList(nullable), null, 7, nullable);
        var inherited = new MembershipCallCounter();
        Check(NativeCollectionMembershipKt.inheritedIndexList(inherited), "needle", "value", inherited);
        var failure = new InvalidOperationException("native index failure");
        var throwing = (IList<string>)NativeCollectionMembershipKt.throwingIndexList(failure);
        Assert.That(Assert.Throws<InvalidOperationException>(() => throwing.IndexOf("value")), Is.SameAs(failure));

        foreach (var value in new[] {
            NativeCollectionMembershipKt.defaultIndexList(),
            NativeCollectionMembershipKt.explicitDefaultIndexList(),
            NativeCollectionMembershipKt.defaultMembershipSubList(),
            new ForeignIndexList(),
        })
        {
            var native = (IList<string>)value;
            Assert.That(native.IndexOf("value"), Is.EqualTo(0));
            Assert.That(native.IndexOf("absent"), Is.EqualTo(-1));
            Assert.That(NativeCollectionMembershipKt.kotlinIndexOf(value, "value"), Is.EqualTo(0));
        }
        Assert.That(NativeCollectionMembershipKt.kotlinIndexOf(new List<string> { "value" }, "value"), Is.EqualTo(0));
    }

    private static void AssertMembership<T>(object value, T needle, T stored, MembershipCallCounter counter)
    {
        var native = (ICollection<T>)value;
        Assert.That(NativeCollectionMembershipKt.kotlinMembership(value, needle), Is.True);
        Assert.That(native.Contains(needle), Is.True);
        Assert.That(native.Contains(stored), Is.False);
        Assert.That(NativeCollectionMembershipKt.kotlinMembership(value, stored), Is.False);
        Assert.That(counter.calls(), Is.EqualTo(4));
        Assert.That(native.Count, Is.EqualTo(1));
        Assert.That(native, Is.SameAs(value));
        Assert.That(native.IsReadOnly, Is.True);
        Assert.Throws<NotSupportedException>(() => native.Add(needle));
    }

    [Test]
    public void NativeContainsUsesAuthoredGenericSetMembership()
    {
        var strings = new MembershipCallCounter();
        AssertMembership(NativeCollectionMembershipKt.stringMembershipSet(strings), "needle", "value", strings);
        var integers = new MembershipCallCounter();
        AssertMembership(NativeCollectionMembershipKt.intMembershipSet(integers), 42, 7, integers);
        var nullable = new MembershipCallCounter();
        AssertMembership<object>(NativeCollectionMembershipKt.nullableMembershipSet(nullable), null, 7, nullable);
    }

    [Test]
    public void NativeContainsPreservesListOverridesInheritanceAndExceptions()
    {
        var listCalls = new MembershipCallCounter();
        var list = NativeCollectionMembershipKt.stringMembershipList(listCalls);
        AssertMembership(list, "needle", "value", listCalls);
        Assert.That(((IList<string>)list).IndexOf("value"), Is.EqualTo(0));
        Assert.That(((IList<string>)list).IndexOf("needle"), Is.EqualTo(-1));
        var inherited = new MembershipCallCounter();
        AssertMembership(NativeCollectionMembershipKt.inheritedMembershipSet(inherited), "base", "value", inherited);
        var failure = new InvalidOperationException("native query failure");
        var throwing = (ICollection<string>)NativeCollectionMembershipKt.throwingMembershipSet(failure);
        Assert.That(Assert.Throws<InvalidOperationException>(() => throwing.Contains("needle")), Is.SameAs(failure));
    }

    [Test]
    public void DefaultMembershipAndSubListsDoNotReenterTheirQuerySlots()
    {
        foreach (var value in new[] {
            NativeCollectionMembershipKt.defaultMembershipSet(),
            NativeCollectionMembershipKt.explicitDefaultMembershipSet(),
            NativeCollectionMembershipKt.defaultMembershipSubList(),
        })
        {
            var native = (ICollection<string>)value;
            Assert.That(native.Contains("value"), Is.True);
            Assert.That(native.Contains("absent"), Is.False);
            Assert.That(NativeCollectionMembershipKt.kotlinMembership(value, "value"), Is.True);
            Assert.That(NativeCollectionMembershipKt.kotlinMembership(value, "absent"), Is.False);
            var copy = new string[1];
            native.CopyTo(copy, 0);
            Assert.That(copy, Is.EqualTo(new[] { "value" }));
        }
    }

    [Test]
    public void NativeDictionaryViewsRetainTheirAuthoredMembershipDelegation()
    {
        var keys = new MembershipCallCounter();
        var values = new MembershipCallCounter();
        var value = NativeCollectionMembershipKt.authoredMembershipMap(keys, values);
        var native = (IDictionary<string, string>)value;
        Assert.That(native.ContainsKey("KEY"), Is.True);
        Assert.That(native.Keys.Contains("KEY"), Is.True);
        Assert.That(native.Keys.Contains("absent"), Is.False);
        Assert.That(NativeCollectionMembershipKt.kotlinKeyMembership(value, "KEY"), Is.True);
        Assert.That(keys.calls(), Is.EqualTo(4));
        Assert.That(native.Values.Contains("VALUE"), Is.True);
        Assert.That(native.Values.Contains("absent"), Is.False);
        Assert.That(NativeCollectionMembershipKt.kotlinValueMembership(value, "VALUE"), Is.True);
        Assert.That(values.calls(), Is.EqualTo(3));
        Assert.That(native.Keys.IsReadOnly && native.Values.IsReadOnly, Is.True);
    }
}
