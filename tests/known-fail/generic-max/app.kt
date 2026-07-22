// Known ilemit runtime-failure repro. This intentionally stays outside the green NUnit gate.
// maxOrNull/minOrNull have Double/Float/generic-T sibling overloads. A DIRECT call
// (listOf(3,1,2).maxOrNull(), concrete element) resolves the generic overload fine. But calling it with
// a `gp:T` element — from inside a generic `fun <T:Comparable<T>> mx(c: Collection<T>) = c.maxOrNull()` —
// makes ilemit's reflected generic-method overload selection bind the `IEnumerable<Double>` sibling
// instead of `maxOrNull<T>(IEnumerable<T>)`, crashing at EntryPointNotFound
// (<>dotkt_KIterable_kotlin_Double.iterator()). kotc emits the correct call (owner=null, method=maxOrNull,
// sig=IEnumerable[gp:T], typeArgs=[gp:T]) and bir2cir attributes it correctly to _CollectionsKt; the
// mis-pick is entirely in ilemit generic-overload resolution when a type-arg is itself a gp: parameter.
fun <T : Comparable<T>> mx(c: Collection<T>): T? = c.maxOrNull()
fun main() {
    println(listOf(3, 1, 2).maxOrNull())   // works (concrete element)
    println(mx(listOf(3, 1, 2)))            // crashes (gp:T element) — the C6 ilemit bug
}
