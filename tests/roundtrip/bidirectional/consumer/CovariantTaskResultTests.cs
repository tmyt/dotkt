using System;
using System.Threading.Tasks;
using NUnit.Framework;
using roundtrip.covarianttaskresults;
using roundtrip.selectedbounds;

public class CovariantTaskResultInteropTests
{
    [Test]
    public async Task ProjectedTextKeepsItsLogicalResultAcrossAssemblies()
    {
        ProjectedTextResult<Concrete> view = new ImportedTextResult();
        Assert.That(await LibraryKt.importedTextLength(view), Is.EqualTo("across assemblies".Length));
        Assert.That(await LibraryKt.importedTextIsString(view), Is.True);
        Assert.That((await view.text<Concrete, Concrete>(new Concrete())).ToString(), Is.EqualTo("across assemblies"));
        await view.completed();
    }

    [TestCase(typeof(ProjectedDerivedResult))]
    [TestCase(typeof(ImportedProjectedResult))]
    [TestCase(typeof(ImportedExactResult))]
    public async Task ProjectedInterfaceReturnsItsDeclaredTaskAcrossSuspension(Type implementation)
    {
        var view = (ProjectedResult<Concrete>)Activator.CreateInstance(implementation);
        Task<TaskResultBase> Start(Task<int> gate) => view.read<Concrete, Concrete>(new Concrete(), gate);
        var source = new TaskCompletionSource<int>();
        var result = Start(source.Task);
        var otherEntries = view is NominalReader<Concrete> nominal ? new[] {
            nominal.read<Concrete, Concrete>(new Concrete(), source.Task),
            LibraryKt.importedNominalRead(nominal, source.Task), LibraryKt.importedConstrainedRead(view, source.Task)
        } : Array.Empty<Task<TaskResultBase>>();
        Assert.That(Array.TrueForAll(otherEntries, entry => !entry.IsCompleted), Is.True);
        Assert.That((object)result, Is.InstanceOf<Task<TaskResultBase>>());
        Assert.That(result.IsCompleted, Is.False);
        source.SetResult(17);
        Assert.That(await result, Is.InstanceOf<TaskResultDerived>());
        foreach (var entry in otherEntries) Assert.That(await entry, Is.InstanceOf<TaskResultDerived>());

        var failure = new InvalidOperationException("after suspension");
        Assert.That(await Start(Task.FromResult(17)), Is.InstanceOf<TaskResultDerived>());
        var faulted = Start(Task.FromException<int>(failure));
        Assert.That(Assert.ThrowsAsync<InvalidOperationException>(async () => { await faulted; }), Is.SameAs(failure));
        Assert.That(await view.nullableUnit<Concrete, Concrete>(), Is.SameAs(kotlin.Unit.INSTANCE));
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
        var shared = new SharedResult();
        Assert.That(await ((NominalResult<Concrete>)shared).value<Concrete, Concrete>(), Is.InstanceOf<TaskResultDerived>());
        Assert.That(await ((ConstrainedResult<Concrete>)shared).value<Concrete, Concrete>(), Is.InstanceOf<TaskResultDerived>());
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
        var imported = new ImportedOrdinaryTextResult();
        TextTaskResult view = imported;
        Assert.That((await view.text()).ToString(), Is.EqualTo("separate"));
        Assert.That(await imported.lengthViaInterface(), Is.EqualTo(8));
        Assert.That(await view.optionalText(false), Is.Null);
        Assert.That(await LibraryKt.forwardOptionalText(view, false), Is.Null);
        var forwarded = await LibraryKt.forwardOptionalText(view, true);
        Assert.That(forwarded, Is.TypeOf<string>());
        Assert.That(forwarded, Is.EqualTo("separate"));
        Assert.That(await view.signal(false), Is.Null);
        Assert.That(await view.signal(true), Is.SameAs(kotlin.Unit.INSTANCE));
        var textMap = imported.GetType().GetInterfaceMap(typeof(TextTaskResult));
        var signal = Array.FindIndex(textMap.InterfaceMethods, method => method.Name == "signal");
        Assert.That(textMap.TargetMethods[signal], Is.EqualTo(imported.GetType().GetMethod("signal")));
    }
}
