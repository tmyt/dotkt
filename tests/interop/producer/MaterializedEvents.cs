using System;
using System.Threading;
using System.Threading.Tasks;

namespace MaterializedEvents;

public delegate int Query(int first, string second, bool third, long fourth);

public class Source {
    private Action<int> changed;
    public int AddCount { get; private set; }
    private int removeCount;
    public int RemoveCount => removeCount;
    public object AddedHandler { get; private set; }
    public object RemovedHandler { get; private set; }
    public Action Removing;
    public bool ThrowOnRemove { get; set; }
    public virtual event Action<int> Changed {
        add { AddCount++; AddedHandler = value; changed += value; }
        remove {
            Interlocked.Increment(ref removeCount); RemovedHandler = value; changed -= value;
            Removing?.Invoke();
            if (ThrowOnRemove) throw new InvalidOperationException("remove");
        }
    }
    public event Action Empty;
    public event Action<object, string> Pair;
    public event Query Requested;
    public void Fire(int value) => changed?.Invoke(value);
    public void FireEmpty() => Empty?.Invoke();
    public void FirePair(object sender, string value) => Pair?.Invoke(sender, value);
    public int Request(int first, string second, bool third, long fourth) => Requested?.Invoke(first, second, third, fourth) ?? -1;
}

public sealed class Derived : Source {
    public int VirtualAdds { get; private set; }
    public override event Action<int> Changed {
        add { VirtualAdds++; base.Changed += value; }
        remove { base.Changed -= value; }
    }
}

public interface ISource { event Action<int> Changed; }
public sealed class ExplicitSource : ISource {
    private Action<int> changed;
    event Action<int> ISource.Changed { add { changed += value; } remove { changed -= value; } }
    public void Fire(int value) => changed?.Invoke(value);
}

public static class Concurrency {
    public static void CloseMany(Action close) => Parallel.For(0, 64, _ => close());
    public static void DeliverAndClose(Source source, Action close) => Parallel.Invoke(
        () => { for (var i = 0; i < 256; i++) source.Fire(i); },
        () => CloseMany(close));
}
