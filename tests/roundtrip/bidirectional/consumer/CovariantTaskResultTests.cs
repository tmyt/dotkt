using System;
using System.Threading.Tasks;
using NUnit.Framework;
using roundtrip.covarianttaskresults;
using roundtrip.selectedbounds;

public class CovariantTaskResultInteropTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ProjectedInterfaceReturnsItsDeclaredTaskAcrossSuspension(bool imported)
    {
        ProjectedResult<Concrete> view = imported ? new ImportedProjectedResult() : new ProjectedDerivedResult();
        Task<TaskResultBase> Start(Task<int> gate) => view.read<Concrete, Concrete>(new Concrete(), gate);
        var source = new TaskCompletionSource<int>();
        var result = Start(source.Task);
        Assert.That((object)result, Is.InstanceOf<Task<TaskResultBase>>());
        Assert.That(result.IsCompleted, Is.False);
        source.SetResult(17);
        Assert.That(await result, Is.InstanceOf<TaskResultDerived>());

        var failure = new InvalidOperationException("after suspension");
        source = new TaskCompletionSource<int>();
        result = Start(source.Task);
        source.SetException(failure);
        Assert.That(Assert.ThrowsAsync<InvalidOperationException>(async () => { await result; }), Is.SameAs(failure));

        source = new TaskCompletionSource<int>();
        Task<object> boxed = view.boxed<Concrete, Concrete>(new Concrete(), source.Task);
        Assert.That((object)boxed, Is.InstanceOf<Task<object>>());
        Assert.That(boxed.IsCompleted, Is.False);
        source.SetResult(0);
        Assert.That(await boxed, Is.SameAs(kotlin.Unit.INSTANCE));
    }
    [Test]
    public async Task DirectAndInterfaceTaskEntriesKeepTheirOwnResultTypes()
    {
        var source = new ProducerStringTask<object>();
        var value = new object();
        Task<string> direct = source.read(value, "direct");
        Task<object> wide = ((AnyTaskResult<object>)source).read(value, "wide");
        Assert.That(await direct, Is.EqualTo("direct"));
        Assert.That(await wide, Is.EqualTo("wide"));
        Assert.That(typeof(ProducerStringTask<object>).GetMethod("read",
            new[] { typeof(object), typeof(string) }).ReturnType, Is.EqualTo(typeof(Task<string>)));
        var map = typeof(ProducerStringTask<object>).GetInterfaceMap(typeof(AnyTaskResult<object>));
        var index = Array.FindIndex(map.InterfaceMethods, method => method.Name == "read"
            && method.GetParameters().Length == 2 && method.GetParameters()[1].ParameterType == typeof(string));
        Assert.That(index, Is.GreaterThanOrEqualTo(0));
        Assert.That(map.TargetMethods[index].ReturnType, Is.EqualTo(typeof(Task<object>)));
    }
}
