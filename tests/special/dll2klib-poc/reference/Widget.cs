namespace Probe;

public interface IAdder
{
    int Add(int value);
}

public class Widget : IAdder
{
    private readonly int _seed;

    public Widget(int seed)
    {
        _seed = seed;
        Value = seed;
        Field = seed * 2;
    }

    public int Value { get; set; }

    public int Field;

    public static int Global { get; set; } = 9;

    public int Add(int value) => _seed + value;

    public string Echo(string value) => value;

    public T Identity<T>(T value) => value;

    public static int Twice(int value) => value * 2;
}
