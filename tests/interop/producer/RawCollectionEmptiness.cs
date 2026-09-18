using System;
using System.Collections;
using System.Collections.Generic;

namespace CollectionStorageInterop;

public static class RawCollectionEmptiness
{
    public static object List(bool empty) => empty ? new ArrayList() : new ArrayList { 7, 9 };
    public static object Collection(bool empty) => empty ? new Queue() : new Queue(new[] { 7, 9 });
    public static object Map(bool empty) => empty ? new Hashtable() : new Hashtable { { 7, 9 } };
    public static CountedRawList Counted(bool empty) => new(empty);
    public static CountedRawList InvocationFailure() => new(false) {
        Failure = new System.Reflection.TargetInvocationException(new InvalidOperationException("inner Count failure"))
    };
    public static object MixedCount() => new RawAndReadOnlyList();
    public static RawAndReadOnlyList GenericFailure() => new() { ThrowOnGenericCount = true };
}

public class RawAndReadOnlyList : ICollection, IReadOnlyList<int>
{
    public bool ThrowOnGenericCount { get; set; }
    public Exception Failure { get; } = new InvalidOperationException("generic Count failure");
    public int RawReads { get; private set; }
    public int GenericReads { get; private set; }
    int ICollection.Count { get { RawReads++; return 0; } }
    int IReadOnlyCollection<int>.Count {
        get { GenericReads++; if (ThrowOnGenericCount) throw Failure; return 2; }
    }
    public int this[int index] => index == 0 ? 7 : 9;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public void CopyTo(Array array, int index) => throw new NotSupportedException();
    public IEnumerator<int> GetEnumerator() { yield return 7; yield return 9; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class CountedRawList : ArrayList
{
    public int Reads { get; private set; }
    public bool ThrowOnCount { get; set; }
    public Exception Failure { get; set; } = new InvalidOperationException("raw Count failure");

    public CountedRawList(bool empty)
    {
        if (!empty) { Add(7); Add(9); }
    }

    public override int Count
    {
        get
        {
            Reads++;
            if (ThrowOnCount) throw Failure;
            return base.Count;
        }
    }

    public override IEnumerator GetEnumerator() => throw new InvalidOperationException("isEmpty must not enumerate");
}
