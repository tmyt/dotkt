// Migrated verify-roundtrip.sh section `roundtrip-generic-operator` (#133) — the library half.
// A Kotlin `operator fun get`/`set` on a GENERIC type. facadegen surfaces the operator bit + clrName:get;
// bir2cir binds the facadegen-injected owner's operator to the plain get/set method Kotlin emitted (NOT the
// BCL get_Item/set_Item indexer). Consumed cross-module: `r[1]` / `r2[0] = x`.
package roundtrip.genop

class Arr<T>(val a: Array<T>) {
    operator fun get(i: Int): T = a[i]
    operator fun set(i: Int, x: T) { a[i] = x }
}
