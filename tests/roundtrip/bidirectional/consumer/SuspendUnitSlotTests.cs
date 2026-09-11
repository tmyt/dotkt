using System;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using roundtrip.suspendunitslot;

public class SuspendUnitSlotTests
{
    private static async Task Complete(Func<UnitGate, Task<kotlin.Unit>> start)
    {
        var gate = new UnitGate();
        var task = start(gate);
        Assert.That(task.IsCompleted, Is.False);
        Assert.That(gate.entries, Is.EqualTo(1));
        gate.release();
        Assert.That(await task, Is.SameAs(kotlin.Unit.INSTANCE));
        Assert.That(gate.entries, Is.EqualTo(1));
    }

    [Test]
    public async Task PlainAndGenericUnitSlotsKeepTheirDistinctTaskSignatures()
    {
        var source = new ImmediateUnitSlot();
        Task ordinary = source.read();
        Task plainSlot = ((PlainUnitSlot)source).read();
        Task<kotlin.Unit> generic = ((UnitSlot<kotlin.Unit>)source).read();
        await ordinary;
        await plainSlot;
        Assert.That(await generic, Is.SameAs(kotlin.Unit.INSTANCE));
        Assert.That(ordinary.IsCompletedSuccessfully, Is.True);
        Assert.That(typeof(ImmediateUnitSlot).GetMethod("read",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).ReturnType,
            Is.EqualTo(typeof(Task)));
        var map = typeof(ImmediateUnitSlot).GetInterfaceMap(typeof(UnitSlot<kotlin.Unit>));
        var index = Array.FindIndex(map.InterfaceMethods, method => method.Name == "read");
        Assert.That(index, Is.GreaterThanOrEqualTo(0));
        Assert.That(map.TargetMethods[index].IsPrivate, Is.True);
        Assert.That(map.TargetMethods[index].ReturnType, Is.EqualTo(typeof(Task<kotlin.Unit>)));
        Assert.That(await SuspendUnitSlotKt.observeLocal(source), Is.SameAs(kotlin.Unit.INSTANCE));
    }

    [Test]
    public async Task GenericInterfaceBaseAndMethodSlotsResumeWithUnit()
    {
        await Complete(gate => ((UnitSlot<kotlin.Unit>)new DelayedUnitSlot(gate)).read());
        await Complete(gate => ((UnitBaseSlot<kotlin.Unit>)new DelayedBaseUnitSlot(gate)).read());
        await Complete(gate => ((MethodUnitSlot<kotlin.Unit>)new GenericMethodUnitSlot(gate)).read("method"));
        var gate = new UnitGate();
        var source = new FurtherUnitSlot(gate);
        var task = ((UnitSlot<kotlin.Unit>)source).read();
        Assert.That(task.IsCompleted, Is.False);
        Assert.That(source.calls, Is.EqualTo(1));
        gate.release();
        Assert.That(await task, Is.SameAs(kotlin.Unit.INSTANCE));
        Assert.That(source.calls, Is.EqualTo(1));
    }

    [Test]
    public async Task ErasedArgumentsAndErasedResultsUseOneCorrectAdapter()
    {
        foreach (var value in new[] { null, kotlin.Unit.INSTANCE })
        {
            var gate = new UnitGate();
            var source = new ErasedParameterUnitSlot(gate);
            var task = ((ErasedUnitSlot<kotlin.Unit>)source).read(value);
            Assert.That(task.IsCompleted, Is.False);
            Assert.That(source.wasNull, Is.EqualTo(value is null));
            gate.release();
            Assert.That(await task, Is.SameAs(kotlin.Unit.INSTANCE));
        }
        var resultGate = new UnitGate();
        var result = ((NullableResultSlot<kotlin.Unit>)new NullableErasedUnitSlot(resultGate)).read();
        Assert.That(result.IsCompleted, Is.False);
        resultGate.release();
        Assert.That(await result, Is.SameAs(kotlin.Unit.INSTANCE));
    }

    [Test]
    public async Task InheritedAbstractAndDefaultBodiesFillGenericSlots()
    {
        Assert.That(await ((UnitSlot<kotlin.Unit>)new InheritedUnitBody()).read(), Is.SameAs(kotlin.Unit.INSTANCE));
        Assert.That(await ((UnitSlot<kotlin.Unit>)new ConcreteUnitBody()).read(), Is.SameAs(kotlin.Unit.INSTANCE));
        var before = SuspendUnitSlotKt.defaultCount();
        Assert.That(await ((UnitSlot<kotlin.Unit>)new DefaultUnitBody()).read(), Is.SameAs(kotlin.Unit.INSTANCE));
        Assert.That(SuspendUnitSlotKt.defaultCount(), Is.EqualTo(before + 1));
    }

    [Test]
    public void FaultsAndCancellationRemainTaskFailuresOnBothPublicFaces()
    {
        foreach (var generic in new[] { false, true })
        {
            var gate = new UnitGate();
            var source = new DelayedUnitSlot(gate);
            Task failure = generic ? ((UnitSlot<kotlin.Unit>)source).read() : source.read();
            Assert.That(failure.IsCompleted, Is.False);
            gate.fail();
            Assert.ThrowsAsync<InvalidOperationException>(async () => { await failure; });
            Assert.That(failure.IsFaulted, Is.True);
            Task cancellation = generic ? ((UnitSlot<kotlin.Unit>)source).read() : source.read();
            Assert.That(cancellation.IsCompleted, Is.False);
            gate.cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => { await cancellation; });
            Assert.That(cancellation.IsCanceled, Is.True);
        }
    }
}
