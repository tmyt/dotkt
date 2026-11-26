using System;
using System.Threading.Tasks;
using roundtrip.inheritedsuspendcovariance;

namespace GenericValueInterop;

public interface IJoinedPicker : roundtrip.selectedbounds.SuspendPicker<roundtrip.selectedbounds.Item>,
    roundtrip.selectedbounds.SuspendPicker<roundtrip.selectedbounds.OtherItem> { }

public static class InheritedSuspendTaskApi
{
    public static object Pick<T>(roundtrip.selectedbounds.SuspendPicker<T> picker, T value)
        where T : RoundtripDependentNative.Base, RoundtripDependentNative.ILeft, RoundtripDependentNative.IRight =>
        picker.pick<T, T>(value).GetAwaiter().GetResult();

    public static Task<object> ThroughConstrainedTask(roundtrip.selectedbounds.SuspendFactory<roundtrip.selectedbounds.Concrete> factory) =>
        factory.make<roundtrip.selectedbounds.Item, roundtrip.selectedbounds.Item>();

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
