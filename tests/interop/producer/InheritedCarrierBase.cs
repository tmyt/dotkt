namespace InheritedCarrierInterop;

public interface IValue<T> { T Read(); }
public interface IStamp { int Stamp { get; } }

public abstract class GenericBase<T> : IValue<T>, IStamp
{
    private readonly T value;
    protected GenericBase(T value) { this.value = value; }
    T IValue<T>.Read() => value;
    int IStamp.Stamp => 44;
}

public abstract class Middle : GenericBase<string>
{
    protected Middle() : base("clr") { }
}
