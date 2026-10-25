using System.Collections;
using System.Collections.Generic;

namespace MutableIteratorInterop;

public static class NativeSetMutations
{
    public static bool RemoveString(object source, string value) => ((ICollection<string>)source).Remove(value);
    public static void ClearString(object source) => ((ICollection<string>)source).Clear();
    public static bool RemoveInt(object source, int value) => ((ICollection<int>)source).Remove(value);
    public static void ClearInt(object source) => ((ICollection<int>)source).Clear();
}

public sealed class NativeOnlyNumbers : IEnumerable<int>
{
    public IEnumerator<int> GetEnumerator() { yield return 7; yield return 9; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
