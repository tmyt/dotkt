namespace StructReceivers;

public interface Key<T> { int Observe(); }

public sealed class TextKey : Key<string>
{
    private readonly Holder? _holder;
    private readonly bool _mutate;
    public TextKey() { }
    public TextKey(Holder holder, bool mutate) { _holder = holder; _mutate = mutate; }
    public int Observe()
    {
        if (_holder is null) return -1;
        _holder.Trace += "o";
        int observed = _holder.Counter.Count;
        if (_mutate) _holder.Counter.Count += 10;
        return observed;
    }
}

public struct Counter
{
    public int Count;
    public Counter(int count) { Count = count; }
    public int Read<T>(Key<T> key) where T : class => ++Count;
    public void Increment<T>(Key<T> key) where T : class { ++Count; }
    public int Observe<T>(Key<T> key, bool fail) where T : class
    {
        ++Count;
        int observed = key.Observe();
        if (fail) throw new System.InvalidOperationException("receiver-write:" + observed);
        return Count * 100 + observed;
    }
    public int Fail<T>(Key<T> key) where T : class
    {
        ++Count;
        throw new System.InvalidOperationException("receiver-write");
    }
    public int Aliased<T>(Key<T> key, ref Counter other) where T : class
    {
        ++Count;
        other.Count += 10;
        return Count;
    }
}

public sealed class Holder
{
    public Counter Counter;
    public string Trace = "";
    public Holder Receiver() { Trace += "r"; return this; }
    public Counter Snapshot() { Trace += "s"; return Counter; }
    public static Counter Make(Holder holder) { holder.Trace += "s"; return holder.Counter; }
    public ref Counter LiveInstance() => ref Counter;
    public TextKey MakeKey(bool mutate) { Trace += "k"; return new TextKey(this, mutate); }
    public TextKey ReplaceAndMakeKey()
    {
        Trace += "k";
        Counter = new Counter(40);
        return new TextKey(this, false);
    }
    public static ref Counter Live(Holder holder) => ref holder.Counter;
}

public struct GenericCounter<S>
{
    public S Value;
    public int Count;
    public GenericCounter(S value) { Value = value; Count = 0; }
    public int Read<T>(Key<T> key) where T : class => ++Count;
}
