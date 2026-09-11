#nullable enable
using System;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using roundtrip.nullablesuspendunit;

public class NullableSuspendUnitTests
{
    [Test]
    public async Task DirectAndImmediateResultsExposeUnitOrNull()
    {
        foreach (var present in new[] { false, true })
        {
            Task<kotlin.Unit?> direct = NullableSuspendUnitKt.directNullableUnit(present);
            Task<kotlin.Unit?> immediate = NullableSuspendUnitKt.immediateNullableUnit(present);
            Assert.That(direct.IsCompletedSuccessfully, Is.True);
            Assert.That(immediate.IsCompletedSuccessfully, Is.True);
            Assert.That(await direct, Is.SameAs(present ? kotlin.Unit.INSTANCE : null));
            Assert.That(await immediate, Is.SameAs(present ? kotlin.Unit.INSTANCE : null));
        }
    }

    [Test]
    public async Task InterfaceAndLocalCallsPreserveActuallySuspendedResults()
    {
        foreach (var present in new[] { false, true })
        {
            var gate = new NullableUnitGate();
            NullableUnitSource source = gate;
            Task<kotlin.Unit?> result = source.read();
            Assert.That(result.IsCompleted, Is.False);
            gate.resume(present);
            Assert.That(await result, Is.SameAs(present ? kotlin.Unit.INSTANCE : null));
            Task<kotlin.Unit?> forwarded = NullableSuspendUnitKt.localNullableUnit(source);
            Assert.That(forwarded.IsCompleted, Is.False);
            gate.resume(present);
            Assert.That(await forwarded, Is.SameAs(present ? kotlin.Unit.INSTANCE : null));
            GenericUnitSource<kotlin.Unit?> genericSource = gate;
            Task<kotlin.Unit?> generic = genericSource.read();
            Assert.That(generic.IsCompleted, Is.False);
            gate.resume(present);
            Assert.That(await generic, Is.SameAs(present ? kotlin.Unit.INSTANCE : null));
        }
    }

    [Test]
    public void DirectAndResumedFailuresAndCancellationKeepTaskStatus()
    {
        var failure = NullableSuspendUnitKt.failNullableUnit();
        Assert.ThrowsAsync<InvalidOperationException>(async () => { await failure; });
        Assert.That(failure.IsFaulted, Is.True);
        var cancellation = NullableSuspendUnitKt.cancelNullableUnit();
        Assert.CatchAsync<OperationCanceledException>(async () => { await cancellation; });
        Assert.That(cancellation.IsCanceled, Is.True);
        var gate = new NullableUnitGate();
        var resumedFailure = gate.read();
        gate.fail();
        Assert.ThrowsAsync<InvalidOperationException>(async () => { await resumedFailure; });
        Assert.That(resumedFailure.IsFaulted, Is.True);
        var resumedCancellation = gate.read();
        gate.cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => { await resumedCancellation; });
        Assert.That(resumedCancellation.IsCanceled, Is.True);
    }

    [Test]
    public async Task PublicSignaturesAndNullabilityDistinguishNullableUnitFromUnit()
    {
        foreach (var method in new[] {
            typeof(NullableSuspendUnitKt).GetMethod("directNullableUnit")!,
            typeof(NullableSuspendUnitKt).GetMethod("immediateNullableUnit")!,
            typeof(NullableSuspendUnitKt).GetMethod("localNullableUnit")!,
            typeof(NullableUnitSource).GetMethod("read")!,
            typeof(NullableUnitGate).GetMethod("read")! })
        {
            Assert.That(method.ReturnType, Is.EqualTo(typeof(Task<kotlin.Unit>)));
            var info = new NullabilityInfoContext().Create(method.ReturnParameter);
            Assert.That(info.ReadState, Is.EqualTo(NullabilityState.NotNull));
            Assert.That(info.GenericTypeArguments[0].ReadState, Is.EqualTo(NullabilityState.Nullable));
        }
        Assert.That(typeof(NullableSuspendUnitKt).GetMethod("ordinaryUnit")!.ReturnType,
            Is.EqualTo(typeof(Task)));
        await NullableSuspendUnitKt.ordinaryUnit();
    }
}
