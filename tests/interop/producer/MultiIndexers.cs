namespace MultiIndexers;

public class Grid
{
    public int Stored { get; private set; } = 17;
    public int this[int row, int column]
    {
        get => Stored + row * 10 + column;
        set => Stored = value - row * 10 - column;
    }
    public int this[int row, string column]
    {
        get => Stored + 100 + row * 10 + column.Length;
        set => Stored = value - 100 - row * 10 - column.Length;
    }
    public int this[int row, int column, int depth]
    {
        get => Stored + row * 100 + column * 10 + depth;
        set => Stored = value - row * 100 - column * 10 - depth;
    }
}

public class ProtectedGrid<K, V>
{
    private V value;
    public K LastKey { get; private set; }
    public int LastColumn { get; private set; }
    public string LastTag { get; private set; }
    public ProtectedGrid(V initial) { value = initial; }
    [System.Runtime.CompilerServices.IndexerName("Cell")]
    protected V this[K key, int column, string tag]
    {
        get => value;
        set {
            LastKey = key;
            LastColumn = column;
            LastTag = tag;
            this.value = value;
        }
    }
}
