// #34: inline splice must fill an OMITTED defaulted param from the callee's default value.
// Three default kinds, each proven on BOTH the take-default and the override path:
//   1. a LAMBDA default  (`= { 100 }`)      — Tier-2 @KotlinDefault `defaultCarrier` (a lifted non-capturing __lambda)
//   2. a CONST default    (`n: Int = 3`)     — Tier-1 `p["default"]`
//   3. a default reading an EARLIER param (`delta = base * 10`) — Tier-2 @KotlinDefault `defaultArgParam` token

inline fun withFallback(
	value: () -> Int,
	fallback: () -> Int = { 100 }
): Int = value() + fallback()

inline fun scaled(
	n: Int = 3,
	body: (Int) -> Int
): Int = body(n)

inline fun offset(
	base: Int,
	delta: Int = base * 10,
	body: (Int) -> Int
): Int = body(base + delta)

// The default lambda BODY itself contains a nested inline call carrying a lambda (`count { }`) — the re-hoisted
// __lambda must be re-walked by the splice engine so that nested callInline is also spliced (Fable R1).
inline fun nested(
	value: () -> Int,
	fallback: () -> Int = { listOf(10, 20, 30).count { it > 15 } }
): Int = value() + fallback()

fun main() {
	println(withFallback({ 5 }))            // 5 + 100 = 105  (lambda default taken)
	println(withFallback({ 5 }, { 1 }))     // 5 + 1   = 6    (lambda default overridden)
	println(scaled { it * 2 })              // 3 * 2   = 6    (const default taken)
	println(scaled(10) { it * 2 })          // 10 * 2  = 20   (const default overridden)
	println(offset(2) { it })               // 2 + 20  = 22   (earlier-param default taken)
	println(offset(2, 5) { it })            // 2 + 5   = 7    (earlier-param default overridden)
	println(nested({ 1 }))                  // 1 + count{>15}=2 = 3  (nested-inline default lambda taken)
	println(nested({ 1 }, { 9 }))           // 1 + 9   = 10   (nested-inline default lambda overridden)
}
