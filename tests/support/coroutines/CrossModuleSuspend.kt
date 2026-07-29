// CROSS-MODULE suspend callees for the operand-order battery in tests/coroutines (SuspendOperandOrderTests.kt).
//
// They live HERE, in a separate assembly, on purpose: a suspend call to a REFERENCED callee is emitted by kotc in
// the `clr*` vocabulary (`clrStatic`/`clrInstance` and, for a generic callee, `clrGenericStatic`/
// `clrGenericInstance`), not as `callStatic`/`callInstance`. Those are a different arm of the operand descriptor
// bir2cir orders a suspension against, and one of them — `clrGenericInstance` — was reachable only when the call
// WAS the suspension, never when it merely CONTAINED one. A same-module fixture cannot exercise any of it.
//
// Each callee GENUINELY suspends (`Task.Delay(1).await()`), which is what makes an operand-order defect
// observable: the state machine only returns COROUTINE_SUSPENDED, and only then reads back the wrong saved
// state, when the cold call really does suspend.
package dotkt.support

import System.Threading.Tasks.Task

/** Cross-module top-level suspend callee — a `clrStatic` suspend call at every call site outside this assembly. */
public suspend fun corOpXAdd(a: Int, b: Int): Int {
    Task.Delay(1).await()
    return a + b
}

/** Cross-module top-level GENERIC suspend callee — a `clrGenericStatic` suspend call. */
public suspend fun <T> corOpXFirst(a: T, b: Int): T {
    Task.Delay(1).await()
    return a
}

/** Cross-module suspend MEMBERS: `clrInstance` for the plain one, `clrGenericInstance` for the generic one. */
public class CorOpXBox(private val base: Int) {
    public suspend fun add(a: Int, b: Int): Int {
        Task.Delay(1).await()
        return base + a + b
    }

    public suspend fun <T> first(a: T, b: Int): T {
        Task.Delay(1).await()
        return a
    }
}

/** A cross-module suspend callee that completes without ever suspending — the synchronous cold-call path. */
public suspend fun corOpXRelay(n: Int): Int = n
