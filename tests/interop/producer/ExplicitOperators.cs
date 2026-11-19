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
    public static bool operator <(OperatorViews.ReadonlyOrder a, Ticket b) => true;
    public static bool operator >(OperatorViews.ReadonlyOrder a, Ticket b) => false;
    public int Value = value;
    public void operator ++() => Value++;
    public static Ticket op_BitwiseAnd(Ticket ticket, int[] values) => ticket;
    public static Ticket operator &(Ticket ticket, int[,] values) => new(-1);
    public static System.ArraySegment<int> Segment(int value) => new(new[] { value });
}

public interface IAccumulator { void operator +=(int delta); }
public class Accumulator : IAccumulator
{
    public int Total;
    void IAccumulator.operator +=(int delta) => Total += delta;
}
