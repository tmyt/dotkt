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

public class ProtectedGrid<T>
{
    private T stored;
    public ProtectedGrid(T initial) { stored = initial; }
    [System.Runtime.CompilerServices.IndexerName("Entry")]
    protected virtual T this[int key] { get => stored; set => stored = value; }
    public T Read(int key) => this[key];
    public void Write(int key, T value) => this[key] = value;
}

public class ResultBase { }
public class ResultDerived : ResultBase { }
public class NominalGrid
{
    public virtual ResultBase this[int key] => new ResultBase();
}
public class ObjectGrid
{
    public virtual object this[int key] => "base";
}

public class MixedGrid : IGrid<int>
{
    public virtual int this[int key] { get => 17; set { } }
    int IGrid<int>.this[int key] { get => 29; set { } }
}
