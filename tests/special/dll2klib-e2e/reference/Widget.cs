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
