// Cross-file half of #395. The declarations intentionally reverse the order used by the main fixture.
internal fun MutableMap<Int, Int>.crossFileIdentity(): Int = 202
@kotlin.clr.ClrName("crossFileIdentityReadOnlyMap")
internal fun Map<Int, Int>.crossFileIdentity(): Int = 201

internal val MutableMap<Int, Int>.crossFileIdentityProperty: Int get() = 204
@get:kotlin.clr.ClrName("crossFileIdentityPropertyReadOnlyMap")
internal val Map<Int, Int>.crossFileIdentityProperty: Int get() = 203

// Distinct open owner slots can close to the same CLR type (`G<Int, Int>`). The physical allocator therefore keeps
// their frontend-selected declarations separate even though ilemit represents both open slots with one link wildcard.
internal class GenericParameterSignatureProbe<A, B> {
    fun select(value: A): Int = 1
    fun select(value: B): Int = 2
}

// NullableGenericErasure lowers both parameters to object. The selected declaration must also survive the synthesized
// existential surface used by G<*>; that surface is a physical projection, not a second overload-resolution site.
internal class NullableGenericIdentityProbe<T> {
    fun select(value: T?): Int = 3
    @kotlin.clr.ClrName("selectAnyNullable")
    fun select(value: Any?): Int = 4
}

internal class StarIndependentIdentityProbe<T> {
    fun select(value: Map<Int, Int>): Int = 5
    @kotlin.clr.ClrName("selectMutableMap")
    fun select(value: MutableMap<Int, Int>): Int = 6
}
