using System;
using System.Threading.Tasks;
using NUnit.Framework;
using roundtrip.nullablesuspendstruct;

public class NullableSuspendStructTests
{
    [Test]
    public async Task NullableStructTaskSignatureAndSynchronousValues()
    {
        Assert.That(typeof(NullableSuspendStructKt).GetMethod("nullableSegment")!.ReturnType,
            Is.EqualTo(typeof(Task<ArraySegment<string>?>)));
        Task<ArraySegment<string?>?> absent = NullableSuspendStructKt.nullableSegment(false);
        Assert.That(await absent, Is.Null);
        var present = await NullableSuspendStructKt.nullableSegment(true);
        Assert.That(present.HasValue, Is.True);
        Assert.That(present.Value.Count, Is.EqualTo(2));
        Assert.That(present.Value[0], Is.Null);
        Assert.That(present.Value[1], Is.EqualTo("value"));
    }

    [Test]
    public async Task NullableStructTaskPreservesSuspendedCompletion()
    {
        foreach (var present in new[] { false, true })
        {
            var gate = new SegmentGate();
            Task<ArraySegment<string?>?> result = NullableSuspendStructKt.delayedSegment(gate);
            Assert.That(result.IsCompleted, Is.False);
            gate.complete(present);
            var value = await result;
            Assert.That(value.HasValue, Is.EqualTo(present));
            if (present) Assert.That(value!.Value[1], Is.EqualTo("resumed"));
        }
    }

    [Test]
    public async Task NullableStructAbstractSlotKeepsNullableTask()
    {
        SegmentSource source = new SegmentSourceImpl();
        Task<ArraySegment<string?>?> absent = source.read(false);
        Assert.That(await absent, Is.Null);
        Assert.That((await source.read(true))!.Value.Count, Is.EqualTo(2));
    }
}
