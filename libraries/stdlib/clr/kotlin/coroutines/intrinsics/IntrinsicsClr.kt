// The CLR coroutine kickoff intrinsics (bundle-6 P1) over the cold-core SM protocol
// (kotlin.coroutines.clr.internal — docs/design-coroutine-cold-core-task-bridge.md §11): a suspend
// function VALUE is (post-P3) a cold state-machine instance extending BaseContinuationImpl, so
//   createCoroutineUnintercepted  = sm.create(completion)             [REAL body now]
//   startCoroutineUninterceptedOrReturn = sm.create(...).invokeSuspend(Unit)  [REAL for all arities: 0/1
//     use the fixed create() fast paths; arity >= 2 uses the general create(args, completion) protocol via
//     startSuspendUninterceptedOrReturnN(fn, arrayOf(args...), completion)]
//   intercepted                   = ContinuationImpl.intercepted() — real interceptor dispatch (#7 Part B):
//                                    context[ContinuationInterceptor]?.interceptContinuation(this) ?: this
// Until P3 lands, suspend values are NOT SMs, so create/start throw a precise NotImplementedError
// (from clr.internal.notAStateMachine) instead of silently misbehaving.
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.coroutines.intrinsics

import kotlin.coroutines.*
import kotlin.coroutines.clr.internal.BaseContinuationImpl
import kotlin.coroutines.clr.internal.ContinuationImpl
import kotlin.coroutines.clr.internal.notAStateMachine
import kotlin.coroutines.clr.internal.startSuspendUninterceptedOrReturn
import kotlin.coroutines.clr.internal.startSuspendUninterceptedOrReturnN
import kotlin.internal.InlineOnly

@SinceKotlin("1.3")
@InlineOnly
public actual inline fun <T> (suspend () -> T).startCoroutineUninterceptedOrReturn(
    completion: Continuation<T>
): Any? = startSuspendUninterceptedOrReturn(this, completion)

@SinceKotlin("1.3")
@InlineOnly
public actual inline fun <R, T> (suspend R.() -> T).startCoroutineUninterceptedOrReturn(
    receiver: R,
    completion: Continuation<T>
): Any? = startSuspendUninterceptedOrReturn(this, receiver, completion)

@InlineOnly
internal actual inline fun <R, P, T> (suspend R.(P) -> T).startCoroutineUninterceptedOrReturn(
    receiver: R,
    param: P,
    completion: Continuation<T>
): Any? = startSuspendUninterceptedOrReturnN(this, arrayOf<Any?>(receiver, param), completion) // arity-2 via the N-arg protocol

@SinceKotlin("1.3")
public actual fun <T> (suspend () -> T).createCoroutineUnintercepted(
    completion: Continuation<T>
): Continuation<Unit> {
    val sm = this as? BaseContinuationImpl ?: notAStateMachine("createCoroutineUnintercepted")
    return sm.create(completion)
}

@SinceKotlin("1.3")
public actual fun <R, T> (suspend R.() -> T).createCoroutineUnintercepted(
    receiver: R,
    completion: Continuation<T>
): Continuation<Unit> {
    val sm = this as? BaseContinuationImpl ?: notAStateMachine("createCoroutineUnintercepted")
    return sm.create(receiver, completion)
}

@SinceKotlin("1.3")
public actual fun <T> Continuation<T>.intercepted(): Continuation<T> =
    (this as? ContinuationImpl)?.intercepted() as? Continuation<T> ?: this
