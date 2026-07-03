// A value-type-nullable (`Int?`) argument passed to a REFERENCED method's `object` param must be BOXED.
// Regression: `n.toString()` on an `Int?` resolves to a referenced stdlib fn taking `object`; EmitCallArgs'
// `pt==null` (referenced-method) path emitted the arg raw (no `box Nullable<int>`) -> InvalidProgramException.
// `box Nullable<int>` yields the boxed underlying value, or a real null ref when HasValue=false.
fun takeAny(x: Any?): String = x.toString()
fun main() {
    val n: Int? = 5
    println(n.toString())      // 5
    val m: Int? = null
    println(m.toString())      // null
    val i = 7                  // a plain value passed to a reference (Any?) param -> must box
    println(takeAny(i))        // 7
    println(takeAny(n))        // 5
    println(takeAny(m))        // null
}
