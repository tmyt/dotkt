using roundtrip.propertymetadata;

namespace PropertyMetadataInterop;

public interface IValue<T> { T Value { get; } }

public class CarrierMix<X, Y> : CarrierBase<Y>, IValue<int> where Y : notnull
{
    // The Kotlin declaration requires List<Y>; native arrays do not acquire
    // Kotlin collection membership merely because CLR exposes IList<T> on them.
    public CarrierMix(Y value) : base(new System.Collections.Generic.List<Y> { value }) { }
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
