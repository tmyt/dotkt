namespace InheritedIndexers;

public class PublicBase
{
    private int value = 17;
    public int this[int index]
    {
        get => value + index;
        set => this.value = value - index;
    }
}

public class ProtectedBase
{
    private int value = 263;
    [System.Runtime.CompilerServices.IndexerName("Slot")]
    protected int this[int index]
    {
        get => value + index;
        set => this.value = value - index;
    }
}

public class GenericBase<K, V>
{
    private V value;
    public GenericBase(V initial) { value = initial; }
    [System.Runtime.CompilerServices.IndexerName("Entry")]
    protected V this[K key] { get => value; set => this.value = value; }
}

public class PublicGenericBase<K, V>
{
    private V value;
    public PublicGenericBase(V initial) { value = initial; }
    public V this[K key] { get => value; set => this.value = value; }
}
