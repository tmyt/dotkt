// #86 — the crossing at the implementing position, on a CONSTRUCTED generic base.
//
// The slot is only recognisable in the frame the deriving type constructs it in: the reflected declaration says
// `Put(!0, List<int?>)` and the override says `Put(String, List<object>)`, so a comparison made against the OPEN
// declaration disagrees at the type variable and lets the uninhabitable override through. The supertype's arguments
// are substituted first, exactly as the override-slot bridge substitutes them.
import plainnet.GBase
import System.Collections.Generic.List

class CConstructed : GBase<String>() {
    override fun Put(tag: String, values: List<Int?>): String = "K:" + tag
}

fun main() {
    println(CConstructed().toString().substring(0, 2))
}
