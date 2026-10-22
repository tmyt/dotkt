using System;
using System.Collections.Generic;
using NUnit.Framework;

public class NativeCollectionMembershipTests
{
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
        AssertMembership<int?>(NativeCollectionMembershipKt.nullableMembershipSet(nullable), null, 7, nullable);
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
}
