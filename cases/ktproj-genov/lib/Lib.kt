// #25: a GENERIC top-level function consumed CROSS-MODULE (through a <ProjectReference>) among a
// same-name overload set that mixes non-generic siblings + a defaulted-param generic sibling. This is
// the reduced kotlinx-atomicfu `atomic` shape: several non-generic `atomic(Int/Long/Boolean/Double)`
// overloads + a generic `atomic(T): Ref<T>` + a defaulted-sibling `atomic(T, trace: TraceBase = None):
// Ref<T>` + a sole-generic array factory `arrOf<T>(n): Arr<T>`. kotc emits the generic call as
// `callStatic typeArgs=[…] shapeTypes=[tv method 0]` WITHOUT a resolved `sig`/`ret`; bir2cir must stamp
// the resolved signature (from the matched overload's declared shape, typeArgs substituted) so ilemit
// binds `atomic<String?>(null)` to the ARITY-1 `atomic(T)` — NOT the arity-2 defaulted sibling (which
// would pass the non-const default `None` as null) — and finds `arrOf`'s sole overload.
//
// The Ref/Arr bodies carry a plain non-generic tag (no T-typed field) so the case isolates the
// overload-binding fault, not generic-value boxing.
package kotlinx.genov

class Ref<T>(val tag: String)
class Arr<T>(val size: Int)

sealed class TraceBase
object None : TraceBase()

fun atomic(x: Int): Ref<Int> = Ref("int")
fun atomic(x: Long): Ref<Long> = Ref("long")
fun atomic(x: Boolean): Ref<Boolean> = Ref("bool")
fun atomic(x: Double): Ref<Double> = Ref("double")

fun <T> atomic(x: T): Ref<T> = Ref("gen1")
fun <T> atomic(x: T, trace: TraceBase = None): Ref<T> = Ref("gen2:" + (trace === None))

fun <T> arrOf(n: Int): Arr<T> = Arr(n)
