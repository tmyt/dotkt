// #157 (was #80-residual): a NON-suspend member fun that reads the top-level val
// kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED directly — the reader that never flows through
// SuspendColdLowering's SM-body canonicalization. Post-#89 kotc emits this HONESTLY as a cross-module top-level
// val read: `callStatic owner=null method=COROUTINE_SUSPENDED prop:get args:[]` (NOT the reading file's class).
// bir2cir MemberCallSubstitution binds it through the GENERAL owner-null top-level resolver — the `prop:get`
// marker reconstructs `get_COROUTINE_SUSPENDED`, then TryResolveTopLevelStatic (single-candidate, the accessor is
// indexed in TopLevelStatics as a file-class static with intrinsic==null) attributes the true declaring
// kotlin.coroutines.intrinsics.IntrinsicsKt owner. There is NO COROUTINE_SUSPENDED special-case (that band-aid was
// deleted as redundant with the general path). The real port hit this as `Builders_commonKt.get_COROUTINE_SUSPENDED not found`.
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED

// Mirrors DispatchedCoroutine.getResult(): a non-suspend member returning the sentinel as Any?.
class Holder {
    fun getState(): Any? = COROUTINE_SUSPENDED
}

fun main() {
    val h = Holder()
    // Both reads resolve to the SAME cached CoroutineSingletons.COROUTINE_SUSPENDED box -> reference identity holds,
    // exactly as getResult()'s own `state === COROUTINE_SUSPENDED` check relies on.
    println(if (h.getState() === COROUTINE_SUSPENDED) 42 else 0)   // 42
}
