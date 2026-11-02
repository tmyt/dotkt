package kotlin.clr

import kotlin.concurrent.atomics.interlockedCompareExchangeRef

/**
 * A closeable subscription returned by [ClrEvent.subscribe].
 *
 * The compiler supplies [handler] and [remove] from the concrete CLR event. Closing the subscription is
 * idempotent and removes exactly the handler instance that was added when the subscription was created.
 */
public class EventSubscription<T>(
    private val handler: T,
    private val remove: (T) -> Unit,
) : AutoCloseable {
    private var closed: Any? = null

    override fun close() {
        if (interlockedCompareExchangeRef(closed, this, null) == null) remove(handler)
    }
}
