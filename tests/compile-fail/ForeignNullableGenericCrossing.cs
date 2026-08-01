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

        // CONTROL: a direct `int?` IS a Kotlin `Int?`.
        public int OrElse(int? x, int d) => x ?? d;

        // CONTROL: a delegate PARAMETER keeps its `Nullable<Int32>` in Kotlin too.
        public string Describe(int? x, Func<int?, string> f) => f(x);
    }
}
