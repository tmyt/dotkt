// #17: a re-imported cross-module Kotlin type whose package starts with `kotlinx.` — the exact shape of
// the atomicfu CLR port (`kotlinx.atomicfu.AtomicInt`). Its `var value: Int` has real get_value/set_value
// accessors. The `kotlinx.` FQN makes bir2cir's NetInteropBinding.ResolveNetType SKIP the owner (that
// prefix is reserved for stdlib binding), so a direct property get/set on it must be lowered to the
// get_<p>/set_<p> accessor call by MemberCallSubstitution — NOT left as a bare `method:"value",prop:"get"`
// that ilemit's external-owner ResolveMethod can't resolve ("method kotlinx.cell.Cell.value() not found").
package kotlinx.cell

class Cell(initial: Int) {
    var value: Int = initial
        get() = field
        set(v) { field = v }

    fun doubled(): Int = value * 2
}

fun makeCell(n: Int): Cell = Cell(n)
