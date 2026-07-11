// #100 H3 regression guard: a nullable-inner collection type-arg (`Map<String, List<Int>?>`) upcast from a
// MutableMap must still collapse its V to the mutable IList face and verify clean — the `?` must not smuggle an
// un-collapsed IReadOnlyList past the Root-V collapse. (bir2cir's ReferenceNullableStrip removes the reference `?`
// before type-lowering, so this shape already collapses correctly; this is the observable-behavior guard for it.)
fun main() {
    val mm = mutableMapOf<String, MutableList<Int>>("a" to mutableListOf(1))
    val ro: Map<String, List<Int>?> = mm
    println(ro)                             // {a=[1]}
}
