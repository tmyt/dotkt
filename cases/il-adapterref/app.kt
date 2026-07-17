// A BOUND/UNBOUND MEMBER reference passed to an INLINE higher-order function where the referent's return type
// needs COERCING to the expected function type (`add` returns Boolean, `forEach` wants `(T) -> Unit`). fir2ir
// inserts an ADAPTER_FOR_CALLABLE_REFERENCE whose bound instance rides as an ExtensionReceiver param; the naive
// bound-ext-ref lowering emitted a top-level `callStatic owner:null method:add` — but `add` is an INSTANCE member,
// so ilemit failed with `static method not found: add` (issue #84 G, the kotlinx.coroutines
// `consumeEach(collection::add)` / `buildList { consumeEach(::add) }` blocker). The adapter must forward to the
// real member as a `callInstance` — emitted by replaying the adapter's own body (adapterRef).

class Sink { fun add(x: Int): Boolean { println("sink $x"); return true } }

// UNBOUND `::add` whose implicit receiver is the enclosing buildList `this: MutableList<Int>` (the `toList` shape).
fun build(src: List<Int>): List<Int> = buildList { src.forEach(::add) }

fun main() {
	// BOUND member ref `s::add` -> inline forEach (adapter: Boolean-returning member coerced to (Int)->Unit)
	val s = Sink()
	listOf(1, 2, 3).forEach(s::add)

	// UNBOUND ::add against the buildList receiver
	for (x in build(listOf(4, 5))) println("built $x")
}
