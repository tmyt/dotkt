using System;
using System.Collections.Generic;

// A GENERIC .NET method in an overload set. Name + generic arity + parameter count leaves both members standing,
// and stamping a fake `void` for "could not narrow it" made the crossing refusal see a member with no problematic
// return at all — while emission, which links by the exact `memberSig` the frontend resolved, picked the right one
// and handed back a `List<Nullable<int32>>` consumed as a `List<object>`.
namespace ovgen
{
    public static class Fac
    {
        public static List<int?> Make<T>(int x) => new List<int?> { x, null };

        // The sibling that shares name, generic arity and parameter count, and crosses nothing. It is driven in
        // tests/interop so the refusal is shown to discriminate rather than to fire on the set.
        public static string Make<T>(string x) => "s:" + x;
    }
}
