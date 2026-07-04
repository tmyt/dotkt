// Non-inlined GENERIC collection building: `.map` / `.add` (whose boolean return goes through the stdlib
// `clrCollAdd<T>` reading `c.size` = `ICollection<!!T>.get_Count` on an OPEN method type-param) + `.size`.
// Locks the bymap/maxOrNull dispatch family's collection analog: an open-generic ICollection member call
// dispatched inside a non-inlined generic body must bind at runtime (no EntryPointNotFound).
fun <T> mapSelf(xs: Array<T>): List<T> = xs.map { it }

fun <T> buildAndCount(xs: Array<T>): Int {
    val out = mutableListOf<T>()
    var added = 0
    for (x in xs) { if (out.add(x)) added++ }   // add's Boolean return -> clrCollAdd -> c.size (ICollection<!!T>.Count)
    return added + out.size
}

fun nonGenMap(n: Int): List<String> = (0 until n).map { "v$it" }   // the groupValues shape (non-generic .map)

fun main() {
    val m = mapSelf(arrayOf("a", "b", "c"))
    println(m.joinToString(","))   // a,b,c
    println(m.size)                // 3
    println(buildAndCount(arrayOf(10, 20)))   // 2 + 2 = 4
    val ng = nonGenMap(3)
    println(ng.joinToString(","))  // v0,v1,v2
    println(ng.size)               // 3
}
