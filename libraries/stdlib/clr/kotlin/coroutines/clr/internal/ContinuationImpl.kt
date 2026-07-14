/*
 * Copyright 2010-2018 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// The CLR coroutine COLD CORE (bundle-6 P1) — a port of kotlin.coroutines.jvm.internal's shape
// (refs/stdlib-jvm-actual/src/kotlin/coroutines/jvm/internal/ContinuationImpl.kt), as PLAIN
// (non-suspend) Kotlin classes. bir2cir-generated suspend state machines (P3, per
// docs/design-coroutine-cold-core-task-bridge.md §11) extend [ContinuationImpl] (named suspend funs)
// or [SuspendLambda] (suspend lambdas); [BaseContinuationImpl.resumeWith] drives the invokeSuspend
// loop + completion chaining + exception capture.
//
// Deliberate deviations from the JVM original:
//  - `invokeSuspend` takes the RAW resume value (`Any?`), not `Result<Any?>` — the locked §11 erasure
//    (`object invokeSuspend(object result)`). The raw protocol IS the JVM's erased form: a success is
//    the plain value, a failure is the boxed `Result.Failure`. SM prologues rethrow a failed resume via
//    [throwOnFailure] (this file), mirroring the JVM SM's `ResultKt.throwOnFailure($result)`.
//  - Everything is `public`: the generated SMs live in OTHER assemblies (app dlls), so the bases must be
//    CLR-public (the JVM uses `@PublishedApi internal`, a JVM-module notion with no CLR equivalent). The
//    `.clr.internal` package name carries the "not user API" contract.
//  - Interceptor dispatch IS implemented (#7 Part B): [ContinuationImpl.intercepted] consults
//    context[ContinuationInterceptor] and wraps `this` via interceptContinuation (cached), and
//    [BaseContinuationImpl.resumeWith] calls [releaseIntercepted] on SM termination — the real JVM protocol.
//    An installed interceptor OWNS resume dispatch, so it takes PRECEDENCE over the raw SynchronizationContext
//    capture at a `Task.await` resume point (SuspendColdLowering routes those resumes through intercepted()).
//  - JVM-isms skipped: Serializable, DebugMetadata, CoroutineStackFrame, probeCoroutine* debug probes, and
//    the FunctionBase/arity toString plumbing.
@file:Suppress("UNCHECKED_CAST")

package kotlin.coroutines.clr.internal

import kotlin.coroutines.Continuation
import kotlin.coroutines.ContinuationInterceptor
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.SafeContinuation
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
import kotlin.coroutines.intrinsics.intercepted

/**
 * The root of every compiled suspend body: holds the [completion] chain link and drives the resume loop.
 *
 * The generated state machine overrides [invokeSuspend]; [resumeWith] (final) runs it, and while the
 * completion is itself a [BaseContinuationImpl] the loop continues iteratively (the JVM's recursion
 * unrolling), otherwise the outcome is handed to the outer [Continuation.resumeWith].
 */
public abstract class BaseContinuationImpl(
    public val completion: Continuation<Any?>?
) : Continuation<Any?> {

    public final override fun resumeWith(result: Result<Any?>) {
        // The loop carries the RAW resume value (result.value: plain value / boxed Result.Failure) —
        // the same erased representation the §11 invokeSuspend contract uses.
        var current: BaseContinuationImpl = this
        var param: Any? = result.value
        while (true) {
            val completion = current.completion!! // fail fast when resuming a continuation without completion
            var outcome: Any?
            try {
                outcome = current.invokeSuspend(param)
                if (outcome === COROUTINE_SUSPENDED) return
            } catch (exception: Throwable) {
                outcome = createFailure(exception) // the raw failure box (kotlin.Result.Failure)
            }
            current.releaseIntercepted() // this SM instance is terminating — release its intercepted continuation
            if (completion is BaseContinuationImpl) {
                // unroll recursion via the loop
                current = completion
                param = outcome
            } else {
                // top-level completion reached — hand over the outcome and stop
                completion.resumeWith(Result(outcome))
                return
            }
        }
    }

    /**
     * One step of the compiled suspend body: [result] is the RAW resumed value (a plain value, or a
     * boxed `Result.Failure` — rethrow it via [throwOnFailure] first). Returns the body's result,
     * or [COROUTINE_SUSPENDED].
     */
    public abstract fun invokeSuspend(result: Any?): Any?

    /**
     * Releases the intercepted continuation when this state machine terminates. No-op on the base
     * ([RestrictedContinuationImpl] pins EmptyCoroutineContext, no interceptor); [ContinuationImpl]
     * overrides it to call the interceptor's [ContinuationInterceptor.releaseInterceptedContinuation].
     * Invoked by [resumeWith] once [invokeSuspend] returns a real (non-suspended) outcome.
     */
    public open fun releaseIntercepted() {}

    /**
     * Instantiates a fresh copy of this (cold) state machine bound to [completion]. Overridden by
     * generated suspend-LAMBDA state machines (P3); named suspend funs create their SM at the call
     * site instead. On the BASE (not only [SuspendLambda]) so that [RestrictedSuspendLambda] SMs and
     * `createCoroutineUnintercepted` share one protocol — exactly the JVM placement.
     */
    public open fun create(completion: Continuation<*>): Continuation<Unit> {
        throw UnsupportedOperationException("create(Continuation) has not been overridden")
    }

    /** The 1-arg form of [create] (extension receiver OR single parameter). */
    public open fun create(value: Any?, completion: Continuation<*>): Continuation<Unit> {
        throw UnsupportedOperationException("create(Any?;Continuation) has not been overridden")
    }

    /**
     * The general (arity >= 2) form of [create]: the N invoke args arrive BOXED in [args]. The JVM has no
     * such slot (arity-2+ suspend lambdas there route through the generated `FunctionN.invoke(...)`); this
     * is the CLR cold-core generalization — a generated N-ary suspend-lambda SM overrides it, allocates a
     * fresh copy bound to [completion], unpacks `args[i]` into its param fields (with unbox/castclass), and
     * returns it. The arity 0/1 [create] overloads stay as the fixed fast paths.
     */
    public open fun create(args: Array<Any?>, completion: Continuation<*>): Continuation<Unit> {
        throw UnsupportedOperationException("create(Array<Any?>;Continuation) has not been overridden")
    }
}

/** Base for state machines of named RESTRICTED suspend functions (`@RestrictsSuspension` scopes). */
public abstract class RestrictedContinuationImpl(
    completion: Continuation<Any?>?
) : BaseContinuationImpl(completion) {
    init {
        completion?.let {
            require(it.context === EmptyCoroutineContext) {
                "Coroutines with restricted suspension must have EmptyCoroutineContext"
            }
        }
    }

    public override val context: CoroutineContext
        get() = EmptyCoroutineContext
}

/** Base for state machines of named suspend functions. */
public abstract class ContinuationImpl(
    completion: Continuation<Any?>?,
    private val _context: CoroutineContext?
) : BaseContinuationImpl(completion) {
    public constructor(completion: Continuation<Any?>?) : this(completion, completion?.context)

    public override val context: CoroutineContext
        get() = _context ?: EmptyCoroutineContext

    // The cached intercepted continuation (JVM parity: interceptContinuation results are cached per SM so
    // every resume of THIS state machine routes through the same intercepted instance).
    private var intercepted: Continuation<Any?>? = null

    /**
     * #7 Part B — the interceptor protocol: consult `context[ContinuationInterceptor]` and wrap `this` via
     * [ContinuationInterceptor.interceptContinuation], caching the result. When an interceptor is installed it
     * OWNS resume dispatch (its intercepted continuation's `resumeWith` decides the resume thread/context), so a
     * `Task.await` resume — routed through `intercepted()` by SuspendColdLowering — goes through the interceptor,
     * taking PRECEDENCE over the raw SynchronizationContext capture. Absent an interceptor this is the identity
     * continuation and the captured-SyncContext (or inline) fallback stands.
     */
    public open fun intercepted(): Continuation<Any?> =
        intercepted
            ?: (context[ContinuationInterceptor]?.interceptContinuation(this) ?: this)
                .also { intercepted = it }

    /**
     * Releases the intercepted continuation on termination (see [BaseContinuationImpl.releaseIntercepted]) —
     * the JVM releaseIntercepted protocol. Only calls back into the interceptor if it actually wrapped `this`.
     */
    public override fun releaseIntercepted() {
        val intercepted = intercepted
        if (intercepted != null && intercepted !== this) {
            context[ContinuationInterceptor]!!.releaseInterceptedContinuation(intercepted)
        }
        this.intercepted = this // mark released — any further intercepted() returns identity (JVM CompletedContinuation intent)
    }
}

/** Base for generated suspend-LAMBDA state machines ([BaseContinuationImpl.create] is their protocol). */
public abstract class SuspendLambda(
    public val arity: Int,
    completion: Continuation<Any?>?
) : ContinuationImpl(completion) {
    public constructor(arity: Int) : this(arity, null)
}

/** Base for generated RESTRICTED suspend-lambda state machines (`sequence {}` etc.). */
public abstract class RestrictedSuspendLambda(
    public val arity: Int,
    completion: Continuation<Any?>?
) : RestrictedContinuationImpl(completion) {
    public constructor(arity: Int) : this(arity, null)
}

// --- the raw resume-value protocol helpers (called by generated SM prologues + the intrinsics) -------

/**
 * Rethrows a FAILED raw resume value (a boxed `kotlin.Result.Failure`); a plain value passes through.
 * The generated SM prologue calls this on its `result` parameter — the CLR analog of the JVM SM's
 * `ResultKt.throwOnFailure($result)`.
 */
public fun throwOnFailure(result: Any?) {
    if (result is Result.Failure) throw result.exception
}

/**
 * Starts a cold arity-0 suspend value [fn] uninterceptedly: runs its state machine to the first
 * suspension. Returns the sync result or [COROUTINE_SUSPENDED]; a sync exception THROWS (it is not
 * captured into the completion — `startCoroutineUninterceptedOrReturn` semantics).
 */
public fun <T> startSuspendUninterceptedOrReturn(fn: Any?, completion: Continuation<T>): Any? {
    val sm = fn as? BaseContinuationImpl ?: notAStateMachine("startCoroutineUninterceptedOrReturn")
    return (sm.create(completion) as BaseContinuationImpl).invokeSuspend(Unit)
}

/** The receiver (arity-1) form of [startSuspendUninterceptedOrReturn]. */
public fun <R, T> startSuspendUninterceptedOrReturn(fn: Any?, receiver: R, completion: Continuation<T>): Any? {
    val sm = fn as? BaseContinuationImpl ?: notAStateMachine("startCoroutineUninterceptedOrReturn")
    return (sm.create(receiver, completion) as BaseContinuationImpl).invokeSuspend(Unit)
}

/**
 * The general (arity >= 2) form of [startSuspendUninterceptedOrReturn]: the N invoke args arrive BOXED in
 * [args]. The cold SM overrides [BaseContinuationImpl.create]`(args, completion)`, unpacking [args] into its
 * param fields; this starts the created SM to its first suspension exactly like the arity 0/1 helpers.
 */
public fun <T> startSuspendUninterceptedOrReturnN(fn: Any?, args: Array<Any?>, completion: Continuation<T>): Any? {
    val sm = fn as? BaseContinuationImpl ?: notAStateMachine("startCoroutineUninterceptedOrReturn")
    return (sm.create(args, completion) as BaseContinuationImpl).invokeSuspend(Unit)
}

// --- F2: cross-module suspendCoroutine app-drive bridges ---------------------------------------------
//
// Our compiler does NOT inline @InlineOnly cross-module, so an APP calling `suspendCoroutine { … }`
// emits a plain call to the (un-inlined) wrapper — its body (Continuation.kt:144-147) is never inlined at
// the call site. bir2cir instead RECONSTRUCTS that body inside the caller's generated state machine. The
// wrapper buffers a synchronous resume through a SafeContinuation, whose 1-arg ctor + getOrThrow are
// `internal` (assembly-only) and thus unreachable from an app SM. These PUBLIC bridges keep the
// SafeContinuation type INSIDE the stdlib while exposing exactly the two operations the reconstructed SM
// needs; the SM passes ITSELF (it is a Continuation) as the delegate, mirroring `SafeContinuation(c.intercepted())`.
public fun newSafeContinuation(delegate: Continuation<Any?>): Continuation<Any?> =
    SafeContinuation(delegate.intercepted())

public fun safeGetOrThrow(safe: Continuation<Any?>): Any? =
    (safe as SafeContinuation<Any?>).getOrThrow()

internal fun notAStateMachine(who: String): Nothing =
    throw NotImplementedError(
        "$who: the suspend function value is not a coroutine state machine " +
        "(non-SM suspend values — fun references, adapted CLR delegates — arrive with bundle-6 P3)"
    )
