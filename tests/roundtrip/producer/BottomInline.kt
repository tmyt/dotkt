package bottominline

class BottomBox<T>(val value: T)

inline fun <R> bottomDispatch(code: Int, done: () -> R, other: () -> R = { error("unexpected") }): R {
    while (true) {
        when (code) {
            0 -> return done()
            1 -> return other()
            else -> continue
        }
    }
}

inline fun <R> bottomForward(code: Int, done: () -> R): R = bottomDispatch(code, done)
inline fun <R> bottomTail(block: () -> R): R = block()
inline fun nullableBottom(block: () -> Nothing?): Nothing? = block()
inline fun bottomSpin(step: () -> Unit): Nothing { while (true) step() }
class BottomOwner<T> { inline fun through(block: () -> T): T = bottomTail(block) }
