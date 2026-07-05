using System;
namespace DotKt.Runtime.CompilerServices {
    // A minimal stand-in for the DotKt round-trip marker facadegen keys on (it matches by attribute FULL NAME only).
    // A real DotKt library carries this embedded per-assembly; declaring it here lets a C# fixture mimic a Kotlin
    // file facade so the top-level-function (`tlfun`) restoration path can be exercised from verify-il.
    [AttributeUsage(AttributeTargets.Class)] public class KotlinFileClassAttribute : Attribute { }
}
namespace N5 {
    // TWO file facades in the SAME package (namespace) with the SAME top-level function name but DIFFERENT arities,
    // as if from DIFFERENT source files -> they share CallableId(N5, "foo"). The A2 registry-removal keyed the
    // file-class map by CallableId ALONE, collapsing to last-put-wins and mis-routing one overload to the wrong file
    // class (a hard ilemit "method not found"). The overload-aware key routes each by the resolved callee's arity.
    [DotKt.Runtime.CompilerServices.KotlinFileClass]
    public static class UtilsKt { public static int foo() => 100; }
    [DotKt.Runtime.CompilerServices.KotlinFileClass]
    public static class HelpersKt { public static int foo(int x) => x + 1; }
}
