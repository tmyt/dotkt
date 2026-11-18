namespace ExplicitOperators;

public struct Counter(int value)
{
    public int Value = value;
    public static bool operator true(Counter x) => x.Value > 0;
    public static bool operator false(Counter x) => x.Value < 0;
    public static Counter operator +(Counter x, int delta) => new(unchecked(x.Value + delta));
    public static Counter operator checked +(Counter x, int delta) => new(checked(x.Value + delta));
    public void operator +=(int delta) => Value += delta;
}

public sealed class Ticket(int value)
{
    public int Value = value;
    public void operator ++() => Value++;
}
