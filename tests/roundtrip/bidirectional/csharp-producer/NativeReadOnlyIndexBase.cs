using System;
using System.Collections;
using System.Collections.Generic;

namespace BidirectionalInterop;

public class NativeReadOnlyIndexBase : IReadOnlyList<string>
{
    public int Count => 1;
    public string this[int index] => index == 0 ? "value" : throw new ArgumentOutOfRangeException(nameof(index));
    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)new[] { "value" }).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
