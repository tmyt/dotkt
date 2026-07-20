// Producer source for the migrated il-tloverload case. TWO file facades in the SAME package (namespace) with the
// SAME top-level function name but DIFFERENT arities, as if from DIFFERENT source files -> they share
// CallableId(N5, "foo"). The overload-aware key routes each by the resolved callee's arity. Own namespace (N5);
// the DotKt round-trip marker attribute (matched by FULL NAME only) is declared here as a plain-C# stand-in.
using System;
namespace DotKt.Runtime.CompilerServices {
    // A minimal stand-in for the DotKt round-trip marker facadegen keys on (it matches by attribute FULL NAME only).
    [AttributeUsage(AttributeTargets.Class)] public class KotlinFileClassAttribute : Attribute { }
}
namespace N5 {
    [DotKt.Runtime.CompilerServices.KotlinFileClass]
    public static class UtilsKt { public static int foo() => 100; }
    [DotKt.Runtime.CompilerServices.KotlinFileClass]
    public static class HelpersKt { public static int foo(int x) => x + 1; }
}
