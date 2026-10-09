package roundtrip.suspendinlineactions

import kotlin.coroutines.Continuation
import kotlin.coroutines.suspendCoroutine

class ActionGate {
    private var pending: Continuation<Unit>? = null
    suspend fun await(): Unit = suspendCoroutine { pending = it }
    fun release() { pending!!.resumeWith(Result.success(Unit)) }
    fun fail(error: Throwable) { pending!!.resumeWith(Result.failure(error)) }
}

class ActionGuard(private val entryGate: ActionGate? = null) {
    var locked = false
        private set
    var entries = 0
        private set
    var exits = 0
        private set
    var heldOwner: Any? = null
        private set

    suspend fun enter(owner: Any?) {
        entryGate?.await()
        check(!locked)
        locked = true
        heldOwner = owner
        entries++
    }

    fun leave(owner: Any?) {
        check(locked && heldOwner === owner)
        locked = false
        heldOwner = null
        exits++
    }
}

public suspend inline fun <T> ActionGuard.withGuard(owner: Any? = null, action: () -> T): T {
    enter(owner)
    try {
        return action()
    } finally {
        leave(owner)
    }
}

class MemberActions {
    public suspend inline fun <T> run(action: () -> T): T = action()
}
