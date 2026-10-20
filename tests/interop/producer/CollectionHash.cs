using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace HashInterop;

public static class HashOracle
{
    public static List<int> MakeList() => new() { 23, 42 };
    public static int NativeHash(object value) => value.GetHashCode();
    public static int IdentityHash(object value) => RuntimeHelpers.GetHashCode(value);
    public static bool HasKey(object storage, object key) => ((IDictionary)storage).Contains(key);
    public static IEqualityComparer<string> IgnoreCaseComparer() => System.StringComparer.OrdinalIgnoreCase;
}

public class HashOnlyList : List<int>
{
    public int Calls { get; private set; }
    public HashOnlyList() : base(new[] { 23, 42 }) { }
    public override int GetHashCode() { Calls++; return 211; }
}

public class HiddenHashList : List<int>
{
    public HiddenHashList() : base(new[] { 23, 42 }) { }
    public new int GetHashCode() => 999;
}
