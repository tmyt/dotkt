using System;
using System.Collections;
using System.Collections.Generic;

namespace MutableMutationInterop;

public sealed class RawFailureList : ArrayList, IList
{
    public readonly InvalidOperationException Failure = new("raw-mutation");
    public int Writes;
    public RawFailureList() { Add(7); Add(9); }
    object IList.this[int index] { get => base[index]; set { Writes++; throw Failure; } }
    void IList.Insert(int index, object value) { Writes++; throw Failure; }
    void IList.RemoveAt(int index) { Writes++; throw Failure; }
}

public sealed class GenericFailureList : List<int>, IList<int>
{
    public readonly InvalidOperationException Failure = new("generic-mutation");
    public int Writes;
    public GenericFailureList() { Add(7); Add(9); }
    int IList<int>.this[int index] { get => base[index]; set { Writes++; throw Failure; } }
    void IList<int>.Insert(int index, int value) { Writes++; throw Failure; }
    void IList<int>.RemoveAt(int index) { Writes++; throw Failure; }
}
