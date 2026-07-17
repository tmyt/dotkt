// A callable reference written inside an inline lambda whose IMPLICIT RECEIVER is the enclosing `buildList { }`
// MutableList. Because `pushDouble` returns Boolean but `forEach` wants `(Int) -> Unit`, fir2ir inserts a
// coercion ADAPTER_FOR_CALLABLE_REFERENCE: a local function whose bound receiver is an ExtensionReceiver value
// parameter named `receiver` (the enclosing buildList `this`), referenced by the adapter body as `receiver.pushDouble(p0)`.
// kotc lifts that adapter to a file-class static method; the lift MUST emit the adapter's RECEIVER parameter (not
// just the Regular params), else the body's `receiver` reference dangles — the "references undeclared local
// 'receiver'" IrSanity fault that blocked the real kotlinx.coroutines flow subsystem
// (Channels_commonKt.__local*_add). `add(bonus)` additionally captures an enclosing local into the same buildList
// lambda, covering both the adapter-receiver capture and an ordinary enclosing-local capture.
fun MutableList<Int>.pushDouble(x: Int): Boolean { add(x * 2); return true }

fun collect(src: List<Int>, bonus: Int): List<Int> = buildList {
	src.forEach(::pushDouble)
	add(bonus)
}

fun main() {
	for (x in collect(listOf(1, 2, 3), 99)) println(x)
}
