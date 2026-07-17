// #80 RESIDUAL (the ALREADY-OWNER'd top-level-val COROUTINE_SUSPENDED read) — distinct from il-suspendintrinsic's
// owner-null suspend-body read. A NON-suspend member fun that reads the intrinsic val
// kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED directly: kotc stamps the read with THIS file's class as owner
// (`callStatic owner=<AppKt> method=COROUTINE_SUSPENDED prop:get args:[]`) — the exact shape the real port hit as
// `kotlinx.coroutines.Builders_commonKt.get_COROUTINE_SUSPENDED not found`. bir2cir MemberCallSubstitution rebinds any
// COROUTINE_SUSPENDED read to the canonical kotlin.coroutines.intrinsics.IntrinsicsKt owner REGARDLESS of the owner
// kotc stamped; without that rebind ilemit sees an unresolvable `<AppKt>.get_COROUTINE_SUSPENDED`.
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED

// Mirrors DispatchedCoroutine.getResult(): a non-suspend member returning the sentinel as Any?. This is the reader
// that never flows through SuspendColdLowering's SM-body canonicalization, so bir2cir's owner-rebind is its only fix.
class Holder {
    fun getState(): Any? = COROUTINE_SUSPENDED
}

fun main() {
    val h = Holder()
    // Both reads resolve to the SAME cached CoroutineSingletons.COROUTINE_SUSPENDED box -> reference identity holds,
    // exactly as getResult()'s own `state === COROUTINE_SUSPENDED` check relies on.
    println(if (h.getState() === COROUTINE_SUSPENDED) 42 else 0)   // 42
}
