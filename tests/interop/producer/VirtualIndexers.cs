namespace VirtualIndexers;

public class Grid
{
    public int Stored { get; private set; } = 17;
    public virtual int this[int key] { get => Stored + key; set => Stored = value - key; }
    public virtual int this[string key] { get => Stored + key.Length; set => Stored = value - key.Length; }
}

public class GenericGrid<K, V>
{
    private V stored;
    public GenericGrid(V initial) { stored = initial; }
    [System.Runtime.CompilerServices.IndexerName("Cell")]
    public virtual V this[K key, int column] { get => stored; set => stored = value; }
}

public class ReorderedGrid<A, B> : GenericGrid<B, A>
{
    public ReorderedGrid(A initial) : base(initial) { }
}

public interface IGrid<T>
{
    T this[int key] { get; set; }
}
