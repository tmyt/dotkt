namespace ExplicitMethodInterop;

public interface IOperations
{
    int Compute(int value);
    string Compute(string value);
    T Echo<T>(T value);
    string Name { get; }
}

public class ExplicitOperations : IOperations
{
    int IOperations.Compute(int value) => value + 10;

    string IOperations.Compute(string value) => value + "!";

    T IOperations.Echo<T>(T value) => value;

    string IOperations.Name => "explicit";
}

public interface IBaseOperation
{
    int BaseCompute(int value);
}

public interface IDerivedOperation : IBaseOperation
{
}

public class InheritedExplicitOperation : IDerivedOperation
{
    int IBaseOperation.BaseCompute(int value) => value + 20;
}

public interface ITransformer<T>
{
    T Transform(T value);
}

public class StringTransformer : ITransformer<string>
{
    string ITransformer<string>.Transform(string value) => value + "?";
}

public interface IPropertySlot
{
    int value { get; set; }
}

public interface IFunctionSlot
{
    int get_value();
    void set_value(int value);
}

// The only concrete implementation of IPropertySlot lives in a private final explicit-interface DIM body. A final
// Kotlin get_value function is non-virtual CLR metadata and therefore does not capture this property slot.
public interface IPrivateDefaultPropertySlot : IPropertySlot
{
    int IPropertySlot.value
    {
        get => 42;
        set { }
    }
}

public interface IInheritedPropertySlot
{
    int inheritedValue { get; }
}

public interface IDerivedPropertySlot : IInheritedPropertySlot
{
}

public interface IReadOnlyObjectPropertySlot
{
    object value { get; }
}

public class PropertySlotBaseValue
{
    public PropertySlotBaseValue(string text) => Text = text;
    public string Text { get; }
}

public class PropertySlotDerivedValue : PropertySlotBaseValue
{
    public PropertySlotDerivedValue(string text) : base(text) { }
}

public interface IReadOnlyNominalPropertySlot
{
    PropertySlotBaseValue value { get; }
}

public abstract class ReadOnlyNominalPropertyBase
{
    public abstract PropertySlotBaseValue value { get; }
}

public abstract class TwoArgumentPropertyBase<T, U>
{
    public abstract U value { get; }
}

public interface IGenericPropertySlot
{
    int marker { get; }
}

public interface IGenericValuePropertySlot<T>
{
    T value { get; set; }
}

public interface IGenericFunctionSlot<T>
{
    T get_value();
    void set_value(T value);
}

public class InheritedFunctionBase
{
    public int get_value() => 70;
    public void set_value(int value) { }
}

public class DerivedInheritedFunctionBase : InheritedFunctionBase
{
}
