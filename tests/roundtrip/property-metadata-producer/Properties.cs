using roundtrip.propertymetadata;

namespace PropertyMetadataInterop;

public interface IValue<T> { T Value { get; } }

public class CarrierMix<X, Y> : CarrierBase<Y>, IValue<int> where Y : notnull
{
    public CarrierMix(Y value) : base(new[] { value }) { }
    int IValue<int>.Value => 17;
}

public class InnerMix : Outer<string>.Base<int>, IValue<int>
{
    public InnerMix() : base(new Outer<string>("outer")) { }
    int IValue<int>.Value => 17;
}

public class NrtBase<T> where T : class
{
    public System.Tuple<T, string?> Value => new(default!, null);
}

public class NrtMix : NrtBase<System.Tuple<string, string>>, IValue<int>
{
    int IValue<int>.Value => 17;
}
