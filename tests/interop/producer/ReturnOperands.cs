namespace ReturnOperands;

public struct Counter
{
    public int Count { get; private set; }
    public Counter(int initial) { Count = initial; }
    public void Bump(int value) { Count += value; }
}

public static class References
{
    public static void Write(ref int slot, int value) { slot = value; }
}
