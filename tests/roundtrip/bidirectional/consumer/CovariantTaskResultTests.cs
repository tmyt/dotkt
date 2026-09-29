using System;
using System.Threading.Tasks;
using NUnit.Framework;
using roundtrip.covarianttaskresults;

public class CovariantTaskResultInteropTests
{
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
