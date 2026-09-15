using System;
using System.Threading.Tasks;
using NUnit.Framework;
using roundtrip.nullablesuspendstruct;

public class NullableSuspendStructTests
{
    [Test]
    public async Task PrimitiveAndEnumResultsKeepNullableValueSignatures()
    {
        Task<int?> number = NullableSuspendStructKt.nullableNumber(false);
        Task<DayOfWeek?> day = NullableSuspendStructKt.nullableDay(false);
        Assert.That(await number, Is.Null);
        Assert.That(await day, Is.Null);
        Assert.That(await NullableSuspendStructKt.nullableNumber(true), Is.EqualTo(42));
        Assert.That(await NullableSuspendStructKt.nullableDay(true), Is.EqualTo(DayOfWeek.Monday));
    }

    [Test]
    public async Task GenericStructResultRetainsItsTypeArgumentAndNullableWrapper()
    {
        Task<ArraySegment<string>?> absent = NullableSuspendStructKt.genericNullableSegment<string>(
            new ArraySegment<string>(new[] { "unused" }), false);
        Assert.That(await absent, Is.Null);
        var value = new ArraySegment<int>(new[] { 7, 8 });
        Task<ArraySegment<int>?> present = NullableSuspendStructKt.genericNullableSegment<int>(value, true);
        Assert.That((await present)!.Value[1], Is.EqualTo(8));
    }

    [Test]
    public async Task NullReturnSurvivesSuspensionInFinally()
    {
        var gate = new SegmentGate();
        Task<ArraySegment<string?>?> result = NullableSuspendStructKt.segmentThroughFinally(gate);
        Assert.That(result.IsCompleted, Is.False);
        gate.complete(true);
        Assert.That(await result, Is.Null);
    }

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
