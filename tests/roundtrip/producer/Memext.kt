// Migrated verify-roundtrip.sh section `roundtrip-memext` — the library half.
// Member-declared extension functions: `class C { fun T.f() }` consumed via `with(c) { x.f() }`. Covers the
// cross-product plain / infix / operator / inline+generic-method / protected, on a generic user receiver.
// Restored via the `,ext` marker (the first param `__self` becomes the extension receiver); the consumer
// dispatches on the enclosing instance with the extension receiver prepended.
package roundtrip.memext

class Box<T>(val value: T) { fun get(): T = value }
open class Lib(val k: Int) {
    fun Box<Int>.boost(): Int = get() + k                          // member extension function
    infix fun Box<Int>.glue(o: Box<Int>): Int = get() + o.get() + k // member extension infix
    operator fun Box<Int>.times(n: Int): Int = get() * n + k        // member extension operator
    inline fun <R> Box<Int>.mapped(f: (Int) -> R): R = f(get())     // member extension + inline + generic method + lambda
    inline fun Box<Int>.boostedBy(f: (Int) -> Int): Int = f(get()) + k // #23 CROSS-MODULE dual-receiver: body reads BOTH the extension receiver (get) AND the dispatch `this@Lib.k`
    protected fun Box<Int>.sshh(): Int = get() * 100 + k           // protected member extension
    fun useProt(b: Box<Int>): Int = b.sshh()                       // protected used internally
}
