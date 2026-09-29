class RemoteInlineSmartCastBox<T>(val value: T)

inline fun <T> RemoteInlineSmartCastBox<T>.visitForever(action: (T) -> Unit): Nothing {
    while (true) action(value)
}

inline fun <T> remoteVisitSmartCast(value: T, action: (T) -> Unit) {
    action(value)
}

inline fun <T> remoteForwardSmartCast(value: T, action: (T) -> Unit) {
    remoteVisitSmartCast(value) { action(it) }
}

inline fun <T> remoteEvaluateSmartCast(value: () -> T, action: (T) -> Unit) {
    action(value())
}

inline fun <T> remoteReceiverSmartCast(value: T, action: T.() -> Unit) {
    value.action()
}
