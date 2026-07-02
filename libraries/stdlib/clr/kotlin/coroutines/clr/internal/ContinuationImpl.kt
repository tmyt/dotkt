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
//  - JVM-isms skipped: releaseIntercepted (interceptors are v1-identity), Serializable, DebugMetadata,
//    CoroutineStackFrame, probeCoroutine* debug probes, and the FunctionBase/arity toString plumbing.
@file:Suppress("UNCHECKED_CAST")

package kotlin.coroutines.clr.internal

import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED

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

    /** v1: interceptor dispatch is out of scope (§11 v1 limits) — the identity continuation. */
    public open fun intercepted(): Continuation<Any?> = this
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
 * The receiver+param (arity-2) form. NOT expressible pre-P3: the [BaseContinuationImpl.create]
 * protocol only covers arities 0/1 (JVM parity — the JVM routes arity-2 through the FunctionN
 * `invoke(r, p, completion)` we do not port); it needs the bir2cir suspend-invoke protocol
 * (bundle-6 P3, the sfunc/delegate path).
 */
public fun <R, P, T> startSuspendUninterceptedOrReturn(fn: Any?, receiver: R, param: P, completion: Continuation<T>): Any? {
    throw NotImplementedError(
        "startCoroutineUninterceptedOrReturn (arity-2): requires the suspend-invoke protocol (bundle-6 P3); " +
        "create() covers arities 0/1 only"
    )
}

internal fun notAStateMachine(who: String): Nothing =
    throw NotImplementedError(
        "$who: the suspend function value is not a coroutine state machine " +
        "(non-SM suspend values — fun references, adapted CLR delegates — arrive with bundle-6 P3)"
    )
