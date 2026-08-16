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
//
// And the shapes that DO declare an uninhabitable slot, kept here rather than only in tests/compile-fail because
// what has to be shown about them is what a Kotlin author may still legally write ALONGSIDE one: deriving without
// being obliged to fill it, overriding a different member of the same overload set, calling the sibling of a
// generic overload set whose other member crosses, deriving below a .NET type that already fills it, and writing a
// body whose ERASED IMAGE coincides with the crossing while it fills a slot of its own. Each of those is a program
// with a valid lowering, and a refusal that answered "does this type inherit such a slot" rather than "must THIS
// type fill THAT slot" rejected them all.
using System;
using System.Collections.Generic;

namespace NvGen
{
    public static class Api
    {
        public static int OrElse(int? x, int d) => x ?? d;

        public static int? Halve(int? x) => x.HasValue ? x.Value / 2 : (int?)null;

        public static string Describe(int? x, Func<int?, string> f) => f(x);

        public static string DescribeTwice(int? x, Func<int?, string> f) => f(x) + "|" + f(null);
    }

    // An ABSTRACT uninhabitable slot. A Kotlin INTERFACE may extend this and an ABSTRACT class may derive from it:
    // neither is instantiable, so neither has to fill it, and neither emits a body in which the slot's parameter
    // would have to be named.
    public interface INotObliged { string Take(List<int?> xs); }

    public abstract class NotObligedBase { public abstract string Take(List<int?> xs); }

    // A CONCRETE virtual overload set, one member of which crosses. Overriding the OTHER member is an ordinary
    // program; the crossing member keeps its own .NET body and is never asked of Kotlin.
    public class OverloadBase
    {
        public virtual string Take(List<int?> xs) => "net-list";

        public virtual string Take(string s) => "net:" + s;
    }

    // A GENERIC overload set, one member of which returns an uninhabitable slot. `Pick<T>(string)` is the sibling
    // that does not, and it must stay callable: bir2cir resolves the declared return through the call's own
    // frontend-resolved declaration parameters rather than stamping a placeholder for the whole set.
    public static class GenFac
    {
        public static List<int?> Pick<T>(int x) => new List<int?> { x, null };

        public static string Pick<T>(string x) => "gen:" + x;
    }

    // The .NET side ALREADY FILLS the crossing slot, and does so EXPLICITLY — whose CLR member name is qualified
    // with the interface, so it lands under a name of its own and the interface's abstract member looked
    // undischarged. A Kotlin class deriving from this owes nothing.
    public class NetFillsIt : INotObliged
    {
        string INotObliged.Take(List<int?> xs) => "net-explicit";

        public string Describe() => "filled";
    }

    // The erased image of a crossing slot is an ORDINARY .NET signature, and a sibling may declare it for real. Then
    // a Kotlin `override fun Take(ys: List<Any?>)` fills THAT slot and owes the `List<int?>` one nothing — the two
    // are different CLR slots that merely look alike after the erasure.
    public class ImageSibling
    {
        public virtual string Take(List<int?> xs) => "net-q";

        public virtual string Take(List<object> ys) => "net-o";
    }

    // A CONCRETE crossing slot with no sibling at all — and the case that says the erasure's record must be READ and
    // not merely counted. `List<int?>` and `List<bool?>` image to the same `List<object>`, and BOTH record their
    // pre-erasure Kotlin type, so a Kotlin body filling a DotKt supertype's `Take(List<Boolean?>)` carries a record
    // exactly as the crossing's own override would. Presence answered for this slot and refused that program; only
    // the recorded type tells the two apart. Nothing obliges a deriving type to fill this one — it has its own body.
    public class ValueImageBase
    {
        public virtual string Take(List<int?> xs) => "net-int";
    }

}
