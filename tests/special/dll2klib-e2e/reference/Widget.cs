namespace Probe;

using Probe.Contracts;

public delegate int Transformer(int value);

public interface IAdder
{
    int Add(int value);
}

internal interface IHiddenControl
{
    int Read();
}

public interface IVisibleControl
{
    int Read();
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
    public int Read() => 23;
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
