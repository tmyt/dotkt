using System;
using System.Collections;
using System.Collections.Generic;

namespace MutableSnapshotInterop;

public sealed class NativeSnapshotFailure : ICollection<object>
{
    public readonly Exception Failure = new InvalidOperationException("native enumeration failed");
    public int EnumerationCalls;
    public int Count => 1;
    public bool IsReadOnly => false;
    public IEnumerator<object> GetEnumerator() { EnumerationCalls++; throw Failure; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Add(object value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(object value) => throw new NotSupportedException();
    public void CopyTo(object[] target, int index) => throw new NotSupportedException();
    public bool Remove(object value) => throw new NotSupportedException();
}
