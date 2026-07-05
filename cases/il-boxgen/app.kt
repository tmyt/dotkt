// C2 boxed-primitive dual-representation via generics (docs/review-kcc-toolchain-2026-07-05.md §2A C2).
// Every line below crashed or silently lost data before the bir2cir/ilemit value-type-generic fixes.
enum class Season { SPRING, SUMMER, AUTUMN }
fun <T : Enum<T>> nameOf(e: T) = e.name

fun main() {
    // getOrPut on MutableMap<Int,primitive>: was 0 / no-insert (SILENT DATA LOSS).
    val m = mutableMapOf<Int, Int>()
    println(m.getOrPut(5) { 42 })
    println(m.size)
    println(m[5])
    println(m.getOrPut(5) { 99 })   // present key -> 42, no overwrite

    // getOrElse present / absent key: present was garbage.
    println(mapOf(1 to 10, 2 to 20).getOrElse(1) { -1 })
    println(mapOf(1 to 10).getOrElse(9) { -1 })

    // compareBy with a primitive selector: was NRE.
    println(listOf(3, 1, 2).sortedWith(compareBy { it }))
    println(listOf(3, 1, 2).sortedByDescending { it })
    val ps = listOf(3 to "c", 1 to "a", 2 to "b")
    println(ps.sortedWith(compareBy { it.first }).map { it.second })

    // Array<Int?> element boxing: was SEGFAULT.
    val a = arrayOf(1, null, 3)
    println(a.toList())
    val b = arrayOfNulls<Int>(3)
    b[0] = 5
    println(b.toList())

    // T : Enum<T> bound generic: was VerificationException.
    println(nameOf(Season.SUMMER))
}
