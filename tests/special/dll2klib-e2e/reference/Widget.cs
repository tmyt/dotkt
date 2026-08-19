namespace Probe;

using Probe.Contracts;

public delegate int Transformer(int value);

public interface IAdder
{
    int Add(int value);
}

internal interface IHiddenControl
{
    int HiddenDefault() => 101;
}

public interface IVisibleControl
{
    int Read() => 23;
}

public interface IPublicDefaultSlot
{
    void M();
}

internal interface IHiddenDefaultProvider : IPublicDefaultSlot
{
    void IPublicDefaultSlot.M() { }
}

public class DefaultCarrier1 : IHiddenDefaultProvider
{
}

public class DefaultCarrier2 : IPublicDefaultSlot, IHiddenDefaultProvider
{
}

public interface IPublicConstructedDefaultProvider<T> : IPublicDefaultSlot
{
    void IPublicDefaultSlot.M() { }
}

internal sealed class HiddenDefaultArgument
{
}

public class ConstructedDefaultCarrier : IPublicConstructedDefaultProvider<HiddenDefaultArgument>
{
}

internal interface IHiddenReabstractProvider : IHiddenDefaultProvider
{
    abstract void IPublicDefaultSlot.M();
}

public interface IPublicReabstractMethodBase
{
    int Required() => 89;
}

public interface IPublicReabstractMethod : IPublicReabstractMethodBase
{
    abstract int IPublicReabstractMethodBase.Required();
}

public interface IPublicReabstractPropertyBase
{
    int RequiredValue => 90;
}

public interface IPublicReabstractProperty : IPublicReabstractPropertyBase
{
    abstract int IPublicReabstractPropertyBase.RequiredValue { get; }
}

public interface IPublicReabstractEventBase
{
    event System.Action<int> RequiredChanged
    {
        add { }
        remove { }
    }
}

public interface IPublicReabstractEvent : IPublicReabstractEventBase
{
    abstract event System.Action<int> IPublicReabstractEventBase.RequiredChanged;
}

public interface IPublicNullabilityDefaultSlot
{
    string? Normalize(string? value);
}

internal interface IHiddenNullabilityDefaultProvider : IPublicNullabilityDefaultSlot
{
#pragma warning disable CS8769, CS8767
    string IPublicNullabilityDefaultSlot.Normalize(string value) => value;
#pragma warning restore CS8769, CS8767
}

public class NullabilityDefaultCarrier : IHiddenNullabilityDefaultProvider
{
}

public interface IPublicDefaultIndexerSlot
{
    int this[int index] { get; }
    int this[string key] { get; }
}

internal interface IHiddenDefaultIndexerProvider : IPublicDefaultIndexerSlot
{
    int IPublicDefaultIndexerSlot.this[int index] => index + 2;
    int IPublicDefaultIndexerSlot.this[string key] => key.Length + 5;
}

public class DefaultIndexerCarrier : IHiddenDefaultIndexerProvider
{
}

public class ExplicitIndexerCarrier : IPublicDefaultIndexerSlot
{
    int IPublicDefaultIndexerSlot.this[int index] => index + 1;
    int IPublicDefaultIndexerSlot.this[string key] => key.Length + 4;
}

public interface IPublicDefaultEventSlot
{
    event System.Action Changed;
}

internal interface IHiddenDefaultEventProvider : IPublicDefaultEventSlot
{
    event System.Action IPublicDefaultEventSlot.Changed
    {
        add { }
        remove { }
    }
}

public class DefaultEventCarrier : IHiddenDefaultEventProvider
{
}

public interface IPublicExplicitEventSlot
{
    event System.Action<int>? Changed;
}

public class ExplicitEventCarrier : IPublicExplicitEventSlot
{
    private System.Action<int>? _changed;

    event System.Action<int>? IPublicExplicitEventSlot.Changed
    {
        add => _changed += value;
        remove => _changed -= value;
    }

    public void Raise(int value) => _changed?.Invoke(value);
}

public class ExternalExplicitEventCarrier : IExternalExplicitEventSlot
{
    private System.Action<int>? _changed;

    event System.Action<int>? IExternalExplicitEventSlot.Changed
    {
        add => _changed += value;
        remove => _changed -= value;
    }

    public void Raise(int value) => _changed?.Invoke(value);
}

public class PublicAndExplicitEventCarrier : IPublicExplicitEventSlot
{
    private System.Action<int>? _explicitChanged;

    public event System.Action<int>? Changed;

    event System.Action<int>? IPublicExplicitEventSlot.Changed
    {
        add => _explicitChanged += value;
        remove => _explicitChanged -= value;
    }

    public void RaisePublic(int value) => Changed?.Invoke(value);
    public void RaiseExplicit(int value) => _explicitChanged?.Invoke(value);
}

public interface IPublicExplicitShapeSlot
{
    string? Normalize(string? value = null);
    string? Text { get; }
    string? this[string? key] { get; }
}

public class ExplicitShapeCarrier : IPublicExplicitShapeSlot
{
#pragma warning disable CS8769, CS8767
    string IPublicExplicitShapeSlot.Normalize(string value) => value;
    string IPublicExplicitShapeSlot.Text => null!;
    string IPublicExplicitShapeSlot.this[string key] => key;
#pragma warning restore CS8769, CS8767
}

public interface ILeftExplicitSlot
{
    int Pick();
}

public interface IRightExplicitSlot
{
    int Pick();
}

public class ExplicitCollisionCarrier : ILeftExplicitSlot, IRightExplicitSlot
{
    int ILeftExplicitSlot.Pick() => 1;
    int IRightExplicitSlot.Pick() => 2;
}

public interface IConstructedExplicitSlot<T>
{
    T Read();
    T Value { get; }
}

public class ConstructedExplicitCollisionCarrier :
    IConstructedExplicitSlot<int>, IConstructedExplicitSlot<string>
{
    int IConstructedExplicitSlot<int>.Read() => 21;
    string IConstructedExplicitSlot<string>.Read() => "twenty-two";
    int IConstructedExplicitSlot<int>.Value => 23;
    string IConstructedExplicitSlot<string>.Value => "twenty-four";
}

public interface IPublicAndExplicitMethodSlot
{
    int Read();
}

public class PublicAndExplicitMethodCarrier : IPublicAndExplicitMethodSlot
{
    public int Read() => 25;
    int IPublicAndExplicitMethodSlot.Read() => 26;
}

public interface IReimplementedSlot
{
    int M();
}

public class ExplicitReimplementationBase : IReimplementedSlot
{
    int IReimplementedSlot.M() => 3;
}

public interface ILeftExplicitPropertySlot
{
    int Number { get; set; }
}

public interface IRightExplicitPropertySlot
{
    int Number { get; set; }
}

public class ExplicitPropertyCollisionCarrier : ILeftExplicitPropertySlot, IRightExplicitPropertySlot
{
    private int _leftNumber = 4;
    private int _rightNumber = 5;

    int ILeftExplicitPropertySlot.Number
    {
        get => _leftNumber;
        set => _leftNumber = value;
    }

    int IRightExplicitPropertySlot.Number
    {
        get => _rightNumber;
        set => _rightNumber = value;
    }
}

public interface IReimplementedPropertySlot
{
    int Number { get; set; }
}

public class ExplicitPropertyReimplementationBase : IReimplementedPropertySlot
{
    private int _number = 6;

    int IReimplementedPropertySlot.Number
    {
        get => _number;
        set => _number = value;
    }
}

public interface IPublicAndExplicitPropertySlot
{
    int Number { get; set; }
}

public class PublicAndExplicitPropertyCarrier : IPublicAndExplicitPropertySlot
{
    private int _explicitNumber = 27;
    public int Number { get; set; } = 28;

    int IPublicAndExplicitPropertySlot.Number
    {
        get => _explicitNumber;
        set => _explicitNumber = value;
    }
}

public interface ILeftExplicitIndexerSlot
{
    int this[int index] { get; set; }
}

public interface IRightExplicitIndexerSlot
{
    int this[int index] { get; set; }
}

public class ExplicitIndexerCollisionCarrier : ILeftExplicitIndexerSlot, IRightExplicitIndexerSlot
{
    private int _leftItem = 7;
    private int _rightItem = 8;

    int ILeftExplicitIndexerSlot.this[int index]
    {
        get => _leftItem + index;
        set => _leftItem = value - index;
    }

    int IRightExplicitIndexerSlot.this[int index]
    {
        get => _rightItem + index;
        set => _rightItem = value - index;
    }
}

public interface IReimplementedIndexerSlot
{
    int this[int index] { get; set; }
}

public class ExplicitIndexerReimplementationBase : IReimplementedIndexerSlot
{
    private int _item = 9;

    int IReimplementedIndexerSlot.this[int index]
    {
        get => _item + index;
        set => _item = value - index;
    }
}

public interface IPublicAndExplicitIndexerSlot
{
    int this[int index] { get; set; }
}

public class PublicAndExplicitIndexerCarrier : IPublicAndExplicitIndexerSlot
{
    private int _explicitItem = 29;
    public int this[int index] { get => 30 + index; set { } }

    int IPublicAndExplicitIndexerSlot.this[int index]
    {
        get => _explicitItem + index;
        set => _explicitItem = value - index;
    }
}

public interface ILeftExplicitEventSlot
{
    event System.Action<int>? Updated;
}

public interface IRightExplicitEventSlot
{
    event System.Action<int>? Updated;
}

public class ExplicitEventCollisionCarrier : ILeftExplicitEventSlot, IRightExplicitEventSlot
{
    private System.Action<int>? _left;
    private System.Action<int>? _right;

    event System.Action<int>? ILeftExplicitEventSlot.Updated
    {
        add => _left += value;
        remove => _left -= value;
    }

    event System.Action<int>? IRightExplicitEventSlot.Updated
    {
        add => _right += value;
        remove => _right -= value;
    }

    public void RaiseLeft(int value) => _left?.Invoke(value);
    public void RaiseRight(int value) => _right?.Invoke(value);
}

public interface IReimplementedEventSlot
{
    event System.Action<int>? Updated;
}

public class ExplicitEventReimplementationBase : IReimplementedEventSlot
{
    private System.Action<int>? _updated;

    event System.Action<int>? IReimplementedEventSlot.Updated
    {
        add => _updated += value;
        remove => _updated -= value;
    }

    public void RaiseBase(int value) => _updated?.Invoke(value);
}

public interface IExplicitOverHiddenDefaultSlot
{
    int Resolve();
}

internal interface IHiddenExplicitDefaultProvider : IExplicitOverHiddenDefaultSlot
{
    int IExplicitOverHiddenDefaultSlot.Resolve() => 31;
}

public class ExplicitOverHiddenDefaultCarrier :
    IHiddenExplicitDefaultProvider, IExplicitOverHiddenDefaultSlot
{
    int IExplicitOverHiddenDefaultSlot.Resolve() => 32;
}

public class ProtectedInterfaceOwner
{
    protected interface IState
    {
    }

    public class Impl : IState
    {
    }
}

public interface IPublicGenericDefaultSlot<T>
{
    T Echo(T value);
}

internal interface IHiddenGenericDefaultProvider<T> : IPublicGenericDefaultSlot<T>
{
    T IPublicGenericDefaultSlot<T>.Echo(T value) => value;
}

public class GenericDefaultCarrier : IHiddenGenericDefaultProvider<string>
{
}

public class OpenGenericDefaultCarrier<T> : IHiddenGenericDefaultProvider<T>
{
}

public class ExternalDefaultCarrier : IExternalHiddenDefaultProvider
{
}

public class ExplicitDefaultCarrier : IExternalDefaultSlot
{
    int IExternalDefaultSlot.Value() => 37;
}

public interface IPublicDefaultProperty
{
    int Number { get; }
}

internal interface IHiddenDefaultPropertyProvider : IPublicDefaultProperty
{
    int IPublicDefaultProperty.Number => 41;
}

public class DefaultPropertyCarrier : IHiddenDefaultPropertyProvider
{
}

// C# permits a public class to implement a non-public interface and to close a public
// generic interface over a non-public type. Neither edge is a valid public Kotlin
// supertype, while the ordinary public edges beside them must remain visible.
public sealed class VisibilityProbe :
    IHiddenControl,
    IVisibleControl,
    IVisibleGeneric<string>,
    IVisibleEnvelope<IVisibleGeneric<HiddenContractArgument>>
{
}

public class WidgetBase
{
    public int Inherited { get; set; } = 4;
}

public class Widget : WidgetBase, IAdder
{
    private readonly int _seed;
    private readonly int[] _items = new int[4];

    public Widget(int seed)
    {
        _seed = seed;
        Value = seed;
        Field = seed * 2;
    }

    public int Value { get; set; }

    public int Field;

    public static int Global { get; set; } = 9;

    public int this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    public event Transformer? Changed;

    public int Add(int value) => _seed + value;

    public string Echo(string value) => value;

    public string? MaybeNull(bool yes) => yes ? null : "value";

    public string Required() => "required";

    public int Apply(Transformer transform, int value) => transform(value);

    public int ApplyExternal(ExternalTransformer transform, int value) => transform(value);

    public int ApplyExternalGeneric(
        ExternalGenericTransformer<int, int> transform,
        int value) => transform(value);

    public void Increment(ref int value) => value += _seed;

    public void Raise(int value) => Changed?.Invoke(value);

    public T Identity<T>(T value) => value;

    public static int Twice(int value) => value * 2;

    public static Widget operator +(Widget widget, int value) => new(widget._seed + value);

    public sealed class Nested
    {
        public int Triple(int value) => value * 3;
    }
}

public static class WidgetExtensions
{
    public static int Bump(this Widget widget, int value) => widget.Add(value) + 1;
}
