// area:kotc — the #34 default-arg RESIDUAL: a MEMBER inline fn's non-const defaulted arg must be
// CARRIED at the declaration (@KotlinDefault) so InlineSplice can fill it at an omitting splice.
// Before the fix `carriesKotlinDefault` excluded any fn with a DispatchReceiver (member) and any
// suspend fn, so a member inline fn's Tier-2 default was emitted NEITHER in the [KotlinInline]
// payload's p["default"] (kotc only puts CONST defaults there) NOR as a @KotlinDefault carrier ->
// InlineSplice STEP 5 fail-loud "missing (non-defaulted) arg". This is the kotlinx.coroutines
// `BufferedChannel.sendImpl(... onNoWaiterSuspend = { _,_,_,_ -> error("unexpected") })` shape.

class Box(val base: Int) {
	// sendImpl shape: a MEMBER inline fn, a non-const LAMBDA default reading NEITHER a param NOR the
	// dispatch receiver (a non-capturing `__lambda` -> Tier-2 @KotlinDefault `defaultCarrier`).
	inline fun choose(cond: Boolean, primary: () -> Int, fallback: () -> Int = { -1 }): Int =
		if (cond) primary() else fallback()

	// MEMBER inline fn with a simple-expr default (`= emptyList()`) — a `defaultCarrier` (verbatim BIR,
	// no lift) on a member fn's param.
	inline fun total(extra: List<Int> = emptyList(), body: (List<Int>) -> Int): Int = body(extra)

	// MEMBER inline fn with a CONST default (Tier-1 p["default"]) alongside a member dispatch receiver —
	// proves the member path also carries a Tier-1 const default into the inline payload.
	inline fun scale(factor: Int = 4, body: (Int) -> Int): Int = body(base * factor)

	fun a(): Int = choose(true, { 5 })              // 5  (default not taken, but must SPLICE)
	fun b(): Int = choose(false, { 5 })             // -1 (default lambda invoked)
	fun c(): Int = total { it.size }                // emptyList().size = 0
	fun d(): Int = total(listOf(1, 2, 3)) { it.size } // 3
	fun e(): Int = scale { it + 1 }                 // base*4 + 1
	fun f(): Int = scale(10) { it + 1 }             // base*10 + 1
}

fun main() {
	val box = Box(2)
	println(box.a())   // 5
	println(box.b())   // -1
	println(box.c())   // 0
	println(box.d())   // 3
	println(box.e())   // 2*4 + 1 = 9
	println(box.f())   // 2*10 + 1 = 21
}
