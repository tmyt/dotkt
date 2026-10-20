using System;
using System.Collections;
using System.Collections.Generic;

namespace SetMembershipInterop;

public sealed class ComparerSetAndList : HashSet<string>, IReadOnlyList<object>
{
    public ComparerSetAndList() : base(StringComparer.OrdinalIgnoreCase) { Add("a"); }
    public bool NativeContains(string value) => Contains(value);
    int IReadOnlyCollection<object>.Count => 2;
    object IReadOnlyList<object>.this[int index] => "unrelated";
    IEnumerator<object> IEnumerable<object>.GetEnumerator() =>
        ((IEnumerable<object>)new object[] { "unrelated", "list" }).GetEnumerator();
}

public class ReadonlyComparerSet : IReadOnlySet<string>
{
    private readonly HashSet<string> values = new(StringComparer.OrdinalIgnoreCase) { "a" };
    public int Count => values.Count;
    protected virtual bool ContainsCore(string value) => values.Contains(value);
    bool IReadOnlySet<string>.Contains(string value) => ContainsCore(value);
    public bool NativeContains(string value) => ((IReadOnlySet<string>)this).Contains(value);
    public bool Contains(string value) => false;
    public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException("membership must not enumerate");
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public bool IsProperSubsetOf(IEnumerable<string> other) => values.IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<string> other) => values.IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<string> other) => values.IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<string> other) => values.IsSupersetOf(other);
    public bool Overlaps(IEnumerable<string> other) => values.Overlaps(other);
    public bool SetEquals(IEnumerable<string> other) => values.SetEquals(other);
}

public sealed class ThrowingReadonlyComparerSet : ReadonlyComparerSet
{
    public InvalidOperationException Failure { get; } = new("native membership failure");
    protected override bool ContainsCore(string value) => value == "boom" ? throw Failure : base.ContainsCore(value);
}

public sealed class MutableOnlyComparerSet : ISet<string>
{
    private readonly HashSet<string> values = new(StringComparer.OrdinalIgnoreCase) { "a" };
    public int Count => values.Count;
    public bool IsReadOnly => false;
    bool ICollection<string>.Contains(string value) => values.Contains(value);
    public bool NativeContains(string value) => ((ICollection<string>)this).Contains(value);
    public bool Contains(string value) => false;
    public bool Add(string value) => values.Add(value);
    void ICollection<string>.Add(string value) => values.Add(value);
    public void Clear() => values.Clear();
    public void CopyTo(string[] array, int index) => values.CopyTo(array, index);
    public bool Remove(string value) => values.Remove(value);
    public void ExceptWith(IEnumerable<string> other) => values.ExceptWith(other);
    public void IntersectWith(IEnumerable<string> other) => values.IntersectWith(other);
    public bool IsProperSubsetOf(IEnumerable<string> other) => values.IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<string> other) => values.IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<string> other) => values.IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<string> other) => values.IsSupersetOf(other);
    public bool Overlaps(IEnumerable<string> other) => values.Overlaps(other);
    public bool SetEquals(IEnumerable<string> other) => values.SetEquals(other);
    public void SymmetricExceptWith(IEnumerable<string> other) => values.SymmetricExceptWith(other);
    public void UnionWith(IEnumerable<string> other) => values.UnionWith(other);
    public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException("membership must not enumerate");
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class ZeroIntSet : HashSet<int>
{
    public ZeroIntSet() { Add(0); }
}

public sealed class NullableIntSet : HashSet<int?>
{
    public NullableIntSet() { Add(null); Add(2); }
}

public sealed class ListAndUnrelatedSet : List<string>, IReadOnlySet<int>
{
    private readonly HashSet<int> values = new() { 7 };
    public ListAndUnrelatedSet() { Add("value"); }
    int IReadOnlyCollection<int>.Count => values.Count;
    bool IReadOnlySet<int>.Contains(int value) => values.Contains(value);
    IEnumerator<int> IEnumerable<int>.GetEnumerator() => values.GetEnumerator();
    public bool IsProperSubsetOf(IEnumerable<int> other) => values.IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<int> other) => values.IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<int> other) => values.IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<int> other) => values.IsSupersetOf(other);
    public bool Overlaps(IEnumerable<int> other) => values.Overlaps(other);
    public bool SetEquals(IEnumerable<int> other) => values.SetEquals(other);
}
