package kotlin.clr

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
    private var closed: Boolean = false

    override fun close() {
        if (closed) return
        closed = true
        remove(handler)
    }
}
