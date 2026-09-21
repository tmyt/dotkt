#nullable disable
namespace InheritedPropertyInterop;

public interface IValue<T> { T Value { get; } }
public interface IWritableValue<T> { T Value { get; set; } }

public class PropertyBase<T>
{
    public PropertyBase(T value) { Value = value; }
    public T Value { get; set; }
}

public class PropertyMiddle<A, B> : PropertyBase<B>
{
    public PropertyMiddle(B value) : base(value) { }
}

public class GenericPropertyMix<X, Y> : PropertyMiddle<Y, X>, IValue<X>
{
    private readonly X explicitValue;
    public GenericPropertyMix(X visibleValue, X explicitValue) : base(visibleValue)
    {
        this.explicitValue = explicitValue;
    }
    X IValue<X>.Value => explicitValue;
}

public class DifferentPropertyMix : PropertyBase<int>, IValue<string>
{
    public DifferentPropertyMix() : base(7) { }
    string IValue<string>.Value => "slot";
}

public class MutablePropertyMix<T> : PropertyBase<T>, IWritableValue<T>
{
    private T explicitValue;
    public MutablePropertyMix(T visibleValue, T explicitValue) : base(visibleValue)
    {
        this.explicitValue = explicitValue;
    }
    T IWritableValue<T>.Value { get => explicitValue; set => explicitValue = value; }
}
