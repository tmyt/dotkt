using System;
using System.Threading.Tasks;
using roundtrip.inheritedsuspendcovariance;

namespace GenericValueInterop;

public interface IJoinedPicker : roundtrip.selectedbounds.SuspendPicker<roundtrip.selectedbounds.Item>,
    roundtrip.selectedbounds.SuspendPicker<roundtrip.selectedbounds.OtherItem> { }

public static class InheritedSuspendTaskApi
{
    public static Task<Value> FromWideClass(WideClassFactory factory) => factory.make();
    public static Task<Narrow> FromNarrowClass(NarrowClassFactory factory) => factory.make();
    public static Task<Value> ChooseThroughClass(WideClassFactory factory, Narrow input) => factory.choose<Narrow, Narrow>(input);

    private static T Complete<T>(Task<T> task, IntGate gate)
    {
        if (task.IsCompleted) throw new Exception("Expected a suspended Task");
        gate.resume(1);
        return task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    public static int DirectIntResult(ProducedIntResult child, IntGate gate) => Complete(child.next(3), gate);

    public static int VirtualIntResults(GenericResult<int> child, IntGate gate) =>
        (int)Complete(child.next(4), gate) + (int)Complete(((WideResult<int>)child).next(5), gate);

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
