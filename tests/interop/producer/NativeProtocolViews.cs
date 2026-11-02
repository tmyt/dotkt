namespace NativeProtocolViews;

public sealed class Counter
{
    public int Calls { get; private set; }
    public void Tick() => Calls++;
}

public interface ILeaf : System.IDisposable { }

public class GenericBase<T> : ILeaf
{
    private readonly Counter counter;
    public T Payload { get; }
    public GenericBase(T payload, Counter counter) { Payload = payload; this.counter = counter; }
    public void Dispose() => counter.Tick();
}

public sealed class GenericDerived<T> : GenericBase<T>
{
    public GenericDerived(T payload, Counter counter) : base(payload, counter) { }
}

public readonly struct ValueResource : System.IDisposable
{
    private readonly Counter counter;
    public int Value { get; }
    public ValueResource(int value, Counter counter) { Value = value; this.counter = counter; }
    public void Dispose() => counter.Tick();
}
