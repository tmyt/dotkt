// #36: a `var` of a VALUE-TYPE nullable (`Int?`/`Long?`/`Double?`) that is CAPTURED-and-MUTATED by a lambda is
// promoted by kotc to a heap ref-cell `dotkt$Ref{ var v }` whose field `v` holds the FULL `Nullable<T>` element type.
// Three sites must agree on the Nullable<T> representation: the INIT (`new Ref(init)` ctor arg — must wrap bare `T` ->
// `Nullable<T>`), the smart-cast READ (`if (q != null) … q …` inside an INLINE lambda, where the use is the bare `T` —
// must unwrap `Nullable<T>.Value`), and the WRITE (`q = 7` — must wrap bare `T` -> `Nullable<T>` for the `v` slot).
// Before the fix the ref-cell ctor pushed a bare `int32` into a `Nullable<int32>` ctor slot -> InvalidProgramException.

inline fun run2(b: () -> Unit) { b() }

fun main() {
	// INLINE closure: captured-and-mutated `var Int?` with a smart-cast READ (q -> bare Int) AND a WRITE.
	var q: Int? = 5
	run2 {
		if (q != null) {
			val x: Int = q            // smart-cast READ into a bare-Int slot -> Nullable<int>.Value
			println(x)                // 5
			println(q + 1)            // direct smart-cast READ in an operator -> 6
			q = x + 100               // WRITE bare Int -> Nullable<int> slot
		}
	}
	println(q)                        // 105

	// NON-INLINE closure ref-cells of other value-nullable widths, plus a `null` write.
	var l: Long? = 5L
	var d: Double? = 1.5
	val step = {
		l = (l ?: 0L) + 10L
		d = (d ?: 0.0) + 0.5
	}
	step()
	step()
	println(l)                        // 25
	println(d)                        // 2.5
	l = null
	println(l)                        // null
}
