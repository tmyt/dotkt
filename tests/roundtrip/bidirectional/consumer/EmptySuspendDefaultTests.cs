using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using roundtrip.emptysuspenddefault;

public class EmptySuspendDefaultTests
{
    private sealed class ClrBody : EmptyDefault { }

    [Test]
    public async Task EmptyDefaultIsConcreteOnBothPhysicalEntries()
    {
        var declared = typeof(EmptyDefault).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.That(declared.Length, Is.EqualTo(2));
        Assert.That(declared.All(method => !method.IsAbstract), Is.True);
        Assert.That(typeof(EmptyDefault).GetMethod("read").ReturnType, Is.EqualTo(typeof(Task)));
        var abstractEntries = typeof(AbstractSlot).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.That(abstractEntries.Length, Is.EqualTo(2));
        Assert.That(abstractEntries.All(method => method.IsAbstract), Is.True);
        await ((EmptyDefault)new EmptyBody()).read();
        await ((EmptyDefault)new ClrBody()).read();
        await ((AbstractSlot)new AbstractBody()).read();
        Assert.That(await ((GenericSlot<kotlin.Unit>)new EmptyGenericBody()).read(), Is.SameAs(kotlin.Unit.INSTANCE));
        Assert.That(await EmptySuspendDefaultKt.localEmpty(), Is.SameAs(kotlin.Unit.INSTANCE));
        Assert.That(await EmptySuspendDefaultKt.localGeneric(), Is.SameAs(kotlin.Unit.INSTANCE));
    }

    [Test]
    public async Task NonemptyDefaultRetainsItsSuspendingBody()
    {
        var gate = new DefaultGate();
        var task = ((SuspendingDefault)new SuspendingBody()).read(gate);
        Assert.That(task.IsCompleted, Is.False);
        Assert.That(gate.entries, Is.EqualTo(1));
        gate.release();
        await task;
        Assert.That(task.IsCompletedSuccessfully, Is.True);
        Assert.That(gate.entries, Is.EqualTo(1));
    }
}
