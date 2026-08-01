// #86 — the .NET shapes carrying a NULLABLE VALUE type that Kotlin must keep crossing exactly.
//
// Carrier-argument erasure makes a possibly-value `X?` `System.Object` in a reified ARGUMENT, and the refusal that
// guards the shapes it cannot inhabit (a `List<int?>` parameter) has to be told apart from the shapes it inhabits
// perfectly. Only a RUNNING consumer proves the second half: a refusal that fired one position too wide would make
// this file's members uncallable, and a compile-fail case cannot show that they still work.
//
// Two positions, both inhabited:
//   * a DIRECT `int?` parameter and return — exactly what a Kotlin `Int?` is (`System.Nullable<int32>`);
//   * a `Func<int?, string>` parameter — a delegate PARAMETER keeps its concrete `Nullable<int32>` in the erasure,
//     so the Kotlin lambda bound into it declares the same slot.
using System;

namespace NvGen
{
    public static class Api
    {
        public static int OrElse(int? x, int d) => x ?? d;

        public static int? Halve(int? x) => x.HasValue ? x.Value / 2 : (int?)null;

        public static string Describe(int? x, Func<int?, string> f) => f(x);

        public static string DescribeTwice(int? x, Func<int?, string> f) => f(x) + "|" + f(null);
    }
}
