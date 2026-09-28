class RemoteInlineSmartCastBox<T>(val value: T)

inline fun <T> RemoteInlineSmartCastBox<T>.visitForever(action: (T) -> Unit): Nothing {
    while (true) action(value)
}

inline fun <T> remoteVisitSmartCast(value: T, action: (T) -> Unit) {
    action(value)
}
