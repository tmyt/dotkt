// ktproj-genov (#25): a GENERIC top-level function consumed CROSS-MODULE among a same-name overload set that mixes
// non-generic siblings + a defaulted-param generic sibling — the reduced kotlinx-atomicfu `atomic` shape. kotc emits
// the generic call as `callStatic typeArgs=[…] shapeTypes=[tv method 0]` WITHOUT a resolved `sig`/`ret`; bir2cir must
// stamp the resolved signature (from the matched overload's declared shape, typeArgs substituted) so ilemit binds
// `atomic<String?>(null)` to the ARITY-1 `atomic(T)` — NOT the arity-2 defaulted sibling (which would pass the
// non-const default `None` as null) — and finds `arrOf`'s sole overload. The Ref/Arr bodies carry a plain non-generic
// tag (no T-typed field) so the case isolates the overload-binding fault, not generic-value boxing.
package kotlinx.genov

// `GenovRef`/`GenovArr` (not `Ref`/`Arr`): `Arr` also exists in the MPP producer's kotlinx.genovc, and a
// cross-dll same-simple-name collision made ilverify confuse the two `Arr` types on `arrOf`'s return. Unique
// names keep the two producer assemblies' types distinct. The case tests the generic-overload binding (#25).
class GenovRef<T>(val tag: String)
class GenovArr<T>(val size: Int)

sealed class TraceBase
object None : TraceBase()

fun atomic(x: Int): GenovRef<Int> = GenovRef("int")
fun atomic(x: Long): GenovRef<Long> = GenovRef("long")
fun atomic(x: Boolean): GenovRef<Boolean> = GenovRef("bool")
fun atomic(x: Double): GenovRef<Double> = GenovRef("double")

fun <T> atomic(x: T): GenovRef<T> = GenovRef("gen1")
fun <T> atomic(x: T, trace: TraceBase = None): GenovRef<T> = GenovRef("gen2:" + (trace === None))

fun <T> arrOf(n: Int): GenovArr<T> = GenovArr(n)
