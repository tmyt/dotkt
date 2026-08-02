// Migrated verify-roundtrip.sh section `roundtrip-nrt` (#48) — the library half.
// Tri-state nullability (T / T?) restored via the NullableAttribute byte + a value Nullable<int>. The sharp
// proof is the CONSUMER's COMPILE-ABILITY (a mis-restored nullability makes it fail to compile):
//   - reference NRT byte: non-null String (byte 1) and nullable String? (byte 2).
//   - value structural:   Int? = System.Nullable<int> (a distinct CLR shape that must also round-trip).
package roundtrip.nrt

fun retNonNull(): String = "x"                                     // T  (non-null return, NullableAttribute byte 1)
fun takeNonNull(s: String): Int = s.length                         // T  (non-null param)
fun retNullable(flag: Boolean): String? = if (flag) "y" else null  // T? (nullable return, byte 2)
fun takeNullable(s: String?): Int = s?.length ?: -1                // T? (nullable param — the sharp signal)
fun retNullableInt(flag: Boolean): Int? = if (flag) 1 else null    // value T? = System.Nullable<int> (structural)

// #251 — CONSTRUCTOR parameters carry the same NRT byte as method parameters: a PRIMARY and a SECONDARY ctor param
// here, a CLR nested type's ctor param in tests/roundtrip/bidirectional (a nested Kotlin class is not surfaced to a
// Kotlin consumer by dll2klib, so only the C# lane can name it).
class NullableCtorHolder(val s: String?) {
    constructor(n: Int, tag: String?) : this(tag?.repeat(n))       // secondary ctor: nullable param delegating to this(…)
    fun len(): Int = s?.length ?: -1
}

// A nullable REFERENCE ctor param sitting beside a value `Int?` one: the reference param needs the NRT byte, the value
// param must NOT get one (its structural System.Nullable<int> already carries the nullability).
class NullableValueCtor(val n: Int?, val label: String?) {
    fun sum(): Int = (n ?: -1) + (label?.length ?: -1)
}

// A `kotlin.Unit` node AHEAD of a nullable reference in ONE declaration slot. `Unit` occupies no byte in the flattened
// array (it is the type ECMA `void` projects to, and the reader answers both with one rule — docs/dotkt-semantics.md
// § 9), so the writer must skip it too: give it a byte and the `String?` behind it reads Unit's and re-imports non-null,
// which is a consumer compile error at the `null` in the calling test rather than a runtime difference.
//
// A MEMBER rather than a top-level function on purpose: a cross-module call to a top-level function whose parameter
// carries `Unit` as a TYPE ARGUMENT is refused at emission today, because the call-site descriptor lowers that argument
// to `void` while the declaration keeps `kotlin.Pair<kotlin.Unit, String>` — a descriptor defect unrelated to these
// bytes, and one the member path does not have.
class UnitAheadHolder {
    fun lengthOfSecond(p: Pair<Unit, String?>): Int = p.second?.length ?: -1
}
