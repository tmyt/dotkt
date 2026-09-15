package roundtrip.deferredcapture

inline fun <T> deferredReader(crossinline read: () -> T): () -> T = { read() }

inline fun <T> readerFromUpdatedLocal(initial: T, next: T, transform: (T) -> T): () -> T {
    var current = initial
    val read = deferredReader { current }
    current = transform(next)
    return read
}
