// The .NET SURFACE the Kotlin case beside this file is refused against. A genuine C# API declaring a nullable
// value type INSIDE a generic argument — the one shape carrier-argument erasure (#86) leaves no Kotlin expression
// able to inhabit, because Kotlin's own `List<Int?>` is an `IReadOnlyList<object>` and constructing
// `System.Collections.Generic.List<Int?>()` from Kotlin erases its argument the same way.
//
// The two CONTROLS matter as much as the refused member: a DIRECT `int?` parameter is exactly what a Kotlin `Int?`
// is and must keep crossing, and a `Func<int?, string>` parameter is inhabited exactly too, because a delegate
// PARAMETER keeps its concrete `Nullable<V>` in the erasure. A predicate that called a delegate parameter an
// argument position refused this second one, which is why it is pinned here rather than left to the rule's prose.
using System;
using System.Collections.Generic;

namespace fgn
{
    public class Api
    {
        // REFUSED: `List<Nullable<Int32>>` is not a type any Kotlin expression has.
        public int CountPresent(List<int?> xs)
        {
            int n = 0;
            foreach (var x in xs) if (x.HasValue) n++;
            return n;
        }

        // REFUSED at the RETURN, which is a different channel: the node's own `ret` is the caller's KOTLIN view and
        // is erased as a Kotlin slot, so it reads `List<object>` and the crossing is invisible there. What the member
        // DECLARES has to be stamped beside the parameter vector or this case compiles and leaves a
        // `List<Nullable<Int32>>` on a stack typed as the unrelated Kotlin form.
        public List<int?> Make() { var l = new List<int?>(); l.Add(1); l.Add(null); return l; }

        // #354: an array element is the same reified-argument crossing. Kotlin's `Array<Int?>` is physically
        // `object[]`, not `Nullable<int>[]`, so neither direction has an inhabitable Kotlin value.
        public int CountPresentArray(int?[] xs)
        {
            int n = 0;
            foreach (var x in xs) if (x.HasValue) n++;
            return n;
        }

        public int?[] MakeArray() => new int?[] { 1, null };

        // REFUSED at a PROPERTY, which reaches the same stamp through the accessor.
        public List<int?> Items { get; } = new List<int?>();

        // REFUSED at a GENERIC method's return. These nodes take their parameter descriptor from the frontend and
        // never entered the resolution that establishes a declared return, so this family was unchecked entirely.
        public List<int?> MakeG<T>() { var l = new List<int?>(); l.Add(1); return l; }

        // REFUSED at a genuine public CLR FIELD, which is read through `ldfld` and so carries no parameter vector at
        // all — its declared type is the only thing that states the crossing.
        public List<int?> Storage = new List<int?>();

        // CONTROL: a direct `int?` IS a Kotlin `Int?`.
        public int OrElse(int? x, int d) => x ?? d;

        // CONTROL: a delegate PARAMETER keeps its `Nullable<Int32>` in Kotlin too.
        public string Describe(int? x, Func<int?, string> f) => f(x);
    }
}
