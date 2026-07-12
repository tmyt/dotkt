// #127: `try { value } catch { null }` in VALUE position on a value-type result. The branches assign into a
// shared temp whose DECLARED type is the join type (the try analogue of ternary's `cond` type). A value-type
// null-branch join must materialize the temp as Nullable<T> (HasValue=false on the null branch), NOT assign
// `null` into a bare `int` slot — the same value-type-nullable miscompile class as #56/#126. Covers the direct
// user shape (Int?/Long?/Double?) plus the stdlib `try{v}catch{null}` bindings (toFloatOrNull/toDoubleOrNull),
// value present and null (exception) on each.
fun main() {
    val a: Int? = try { 5 } catch (e: Exception) { null }
    println(a)                                          // 5
    val b: Int? = try { throw RuntimeException("x") } catch (e: Exception) { null }
    println(b)                                          // null

    val l: Long? = try { 7L } catch (e: Exception) { null }
    println(l)                                          // 7
    val d: Double? = try { 3.5 } catch (e: Exception) { null }
    println(d)                                          // 3.5

    // stdlib `String.toFloatOrNull()` / `toDoubleOrNull()` = `try { this.toX() } catch { null }` (value-or-null).
    println("1.5".toFloatOrNull())                      // 1.5
    println("nope".toFloatOrNull())                     // null
    println("2.5".toDoubleOrNull())                     // 2.5
    println("nope".toDoubleOrNull())                    // null

    // Use the recovered null-branch value through arithmetic (would InvalidProgram on a raw-Nullable materialize).
    val c: Int? = try { throw RuntimeException("x") } catch (e: Exception) { null }
    println((c ?: 10) + 1)                              // 11
}
