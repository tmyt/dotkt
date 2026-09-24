using System;
using System.Threading.Tasks;
using roundtrip.inheritedsuspendcovariance;

namespace GenericValueInterop;

public static class InheritedSuspendTaskApi
{
    public static string ThroughInterface(Factory factory, Gate gate)
    {
        Task<Value> task = factory.make();
        if (task.IsCompleted) throw new Exception("The interface Task did not suspend");
        gate.resume(new Narrow("task"));
        if (!task.Wait(TimeSpan.FromSeconds(5))) throw new Exception("The interface Task did not resume");
        return task.GetAwaiter().GetResult().text;
    }

    public static string ThroughBase(Body body, Gate gate)
    {
        Task<Narrow> task = body.make();
        if (task.IsCompleted) throw new Exception("The base Task did not suspend");
        gate.resume(new Narrow("task"));
        if (!task.Wait(TimeSpan.FromSeconds(5))) throw new Exception("The base Task did not resume");
        return task.GetAwaiter().GetResult().text;
    }
}
