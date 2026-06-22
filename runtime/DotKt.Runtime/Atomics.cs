// CLR forms of the kotlinx.atomicfu atomics. The atomicfu compiler plugin erases its wrappers to plain fields +
// (Java) atomic updaters on the JVM; on the CLR we map them to these small Interlocked/Volatile-backed wrappers
// (the "thin actual set" — docs/design-coroutines-clr.md §13a resolution 5). The compiler maps the
// `kotlinx.atomicfu.*` fqnames onto these and `atomic(x)` onto the matching ctor.
using System.Threading;

namespace DotKtx.Atomicfu
{
    public sealed class AtomicInt
    {
        int _v;
        public AtomicInt(int v) { _v = v; }
        public int Value { get => Volatile.Read(ref _v); set => Volatile.Write(ref _v, value); }
        public bool CompareAndSet(int expect, int update) => Interlocked.CompareExchange(ref _v, update, expect) == expect;
        public int GetAndSet(int v) => Interlocked.Exchange(ref _v, v);
        public int IncrementAndGet() => Interlocked.Increment(ref _v);
        public int DecrementAndGet() => Interlocked.Decrement(ref _v);
        public int GetAndIncrement() => Interlocked.Increment(ref _v) - 1;
        public int GetAndDecrement() => Interlocked.Decrement(ref _v) + 1;
        public int AddAndGet(int delta) => Interlocked.Add(ref _v, delta);
        public int GetAndAdd(int delta) => Interlocked.Add(ref _v, delta) - delta;
    }

    public sealed class AtomicLong
    {
        long _v;
        public AtomicLong(long v) { _v = v; }
        public long Value { get => Volatile.Read(ref _v); set => Volatile.Write(ref _v, value); }
        public bool CompareAndSet(long expect, long update) => Interlocked.CompareExchange(ref _v, update, expect) == expect;
        public long GetAndSet(long v) => Interlocked.Exchange(ref _v, v);
        public long IncrementAndGet() => Interlocked.Increment(ref _v);
        public long DecrementAndGet() => Interlocked.Decrement(ref _v);
        public long GetAndIncrement() => Interlocked.Increment(ref _v) - 1;
        public long AddAndGet(long delta) => Interlocked.Add(ref _v, delta);
        public long GetAndAdd(long delta) => Interlocked.Add(ref _v, delta) - delta;
    }

    public sealed class AtomicBoolean
    {
        int _v;
        public AtomicBoolean(bool v) { _v = v ? 1 : 0; }
        public bool Value { get => Volatile.Read(ref _v) != 0; set => Volatile.Write(ref _v, value ? 1 : 0); }
        public bool CompareAndSet(bool expect, bool update) => Interlocked.CompareExchange(ref _v, update ? 1 : 0, expect ? 1 : 0) == (expect ? 1 : 0);
        public bool GetAndSet(bool v) => Interlocked.Exchange(ref _v, v ? 1 : 0) != 0;
    }

    public sealed class AtomicRef<T> where T : class
    {
        T _v;
        public AtomicRef(T v) { _v = v; }
        public T Value { get => Volatile.Read(ref _v); set => Volatile.Write(ref _v, value); }
        public bool CompareAndSet(T expect, T update) => object.ReferenceEquals(Interlocked.CompareExchange(ref _v, update, expect), expect);
        public T GetAndSet(T v) => Interlocked.Exchange(ref _v, v);
    }
}
