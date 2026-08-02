// #86 — the crossing at the implementing position, reached through an INHERITED .NET interface.
//
// The slot is declared on `IBase`; the Kotlin class names `IDerived`. `IDerived.GetMethods()` returns nothing —
// reflection does not hand a derived interface its base's members — so a refusal that inspected each DIRECT
// supertype's own members saw an empty type, let this compile, and the class died at load with "Signature of the
// body and declaration in a method implementation do not match". The obligation is the same at any distance, so the
// supertype graph is walked transitively.
import plainnet.IDerived
import System.Collections.Generic.List

class CInherited : IDerived {
    override fun Take(xs: List<Int?>): String = "I:ok"
}

fun main() {
    println(CInherited().toString().substring(0, 2))
}
