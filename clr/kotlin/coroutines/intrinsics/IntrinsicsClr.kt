// Step-1 CLR stub mirroring the JVM `actual` declarations for this source set.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.coroutines.intrinsics

import kotlin.coroutines.*
import kotlin.internal.InlineOnly

@SinceKotlin("1.3")
@InlineOnly
public actual inline fun <T> (suspend () -> T).startCoroutineUninterceptedOrReturn(
    completion: Continuation<T>
): Any? = TODO("clr binding should be implemented")

@SinceKotlin("1.3")
@InlineOnly
public actual inline fun <R, T> (suspend R.() -> T).startCoroutineUninterceptedOrReturn(
    receiver: R,
    completion: Continuation<T>
): Any? = TODO("clr binding should be implemented")

@InlineOnly
internal actual inline fun <R, P, T> (suspend R.(P) -> T).startCoroutineUninterceptedOrReturn(
    receiver: R,
    param: P,
    completion: Continuation<T>
): Any? = TODO("clr binding should be implemented")

@SinceKotlin("1.3")
public actual fun <T> (suspend () -> T).createCoroutineUnintercepted(
    completion: Continuation<T>
): Continuation<Unit> = TODO("clr binding should be implemented")

@SinceKotlin("1.3")
public actual fun <R, T> (suspend R.() -> T).createCoroutineUnintercepted(
    receiver: R,
    completion: Continuation<T>
): Continuation<Unit> = TODO("clr binding should be implemented")

@SinceKotlin("1.3")
public actual fun <T> Continuation<T>.intercepted(): Continuation<T> = TODO("clr binding should be implemented")
