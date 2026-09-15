using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using roundtrip.nullablegenericsignature;

public class NullableGenericSignatureTests
{
    [Test]
    public void MethodVariablesAndNullableOverloadsRetainExactSignatures()
    {
        var value = new ArraySegment<int>(new[] { 7, 8 });
        ArraySegment<int>? nullable = value;
        Assert.That(NullableGenericSignatureKt.echo<int>(null), Is.Null);
        Assert.That(NullableGenericSignatureKt.echo(nullable)!.Value[1], Is.EqualTo(8));
        Assert.That(NullableGenericSignatureKt.choose(value), Is.EqualTo(2));
        Assert.That(NullableGenericSignatureKt.choose(nullable), Is.EqualTo(12));
        Assert.That(NullableGenericSignatureKt.choose<int>((ArraySegment<int>?)null), Is.EqualTo(-1));
        var overloads = typeof(NullableGenericSignatureKt).GetMethods().Where(m => m.Name == "choose")
            .Select(m => m.MakeGenericMethod(typeof(int)).GetParameters()[0].ParameterType).ToArray();
        Assert.That(overloads, Is.EquivalentTo(new[] { typeof(ArraySegment<int>), typeof(ArraySegment<int>?) }));
    }

    [Test]
    public async Task OwnerAndMethodFramesRemainDistinct()
    {
        var owner = new SegmentEcho<string>();
        var text = new ArraySegment<string>(new[] { "owner" });
        Assert.That(owner.echo(null), Is.Null);
        Assert.That(owner.echo(text)!.Value[0], Is.EqualTo("owner"));
        Assert.That(owner.other<int>(new ArraySegment<int>(new[] { 42 }))!.Value[0], Is.EqualTo(42));
        Assert.That(owner.other<int>(null), Is.Null);
        Task<ArraySegment<string>?> result = owner.suspendEcho(text);
        Assert.That((await result)!.Value[0], Is.EqualTo("owner"));
        Assert.That(await owner.suspendEcho(null), Is.Null);
    }

    [Test]
    public async Task MethodGenericNullableParameterSurvivesRealSuspension()
    {
        foreach (var present in new[] { false, true })
        {
            var gate = new SignatureGate();
            ArraySegment<int>? value = null;
            if (present) value = new ArraySegment<int>(new[] { 9 });
            Task<ArraySegment<int>?> task = NullableGenericSignatureKt.delayedEcho(value, gate);
            Assert.That(task.IsCompleted, Is.False);
            gate.complete();
            var result = await task;
            Assert.That(result.HasValue, Is.EqualTo(present));
            if (present) Assert.That(result!.Value[0], Is.EqualTo(9));
        }
    }
}
