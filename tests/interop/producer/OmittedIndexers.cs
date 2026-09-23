namespace OmittedIndexers;

public class Grid
{
    public int Stored { get; private set; } = 17;
    public int this[int row, int column = 5]
    {
        get => Stored + row * 10 + column;
        set => Stored = value - row * 10 - column;
    }
    public int this[string row, int column = 9]
    {
        get => Stored + row.Length * 10 + column;
        set => Stored = value - row.Length * 10 - column;
    }
}

public class ProtectedGrid<K, V>
{
    private V stored;
    public int LastColumn { get; private set; }
    public K LastKey { get; private set; }
    public ProtectedGrid(V initial) { stored = initial; }
    [System.Runtime.CompilerServices.IndexerName("Cell")]
    protected V this[K key, int column = 7]
    {
        get { LastKey = key; LastColumn = column; return stored; }
        set { LastKey = key; LastColumn = column; stored = value; }
    }
}

public class VarargGrid
{
    private int stored = 17;
    public int LastCount { get; private set; }
    public int this[params int[] keys]
    {
        get { LastCount = keys.Length; return stored + keys.Length; }
        set { LastCount = keys.Length; stored = value - keys.Length; }
    }
}
