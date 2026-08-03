package roundtrip.higherorder

class Box<T>(val value: T)

fun <U, V> applyBox(f: (Box<U>) -> Box<V>, value: Box<U>): Box<V> = f(value)

class Router {
    fun <U, V> route(f: (Box<U>) -> Box<V>, value: Box<U>): Box<V> = f(value)
}

fun <U, V> Box<U>.mapBox(f: (Box<U>) -> Box<V>): Box<V> = f(this)

infix fun <U, V> Box<U>.pipe(f: (Box<U>) -> Box<V>): Box<V> = f(this)

operator fun <U, V> Box<U>.times(f: (Box<U>) -> Box<V>): Box<V> = f(this)

inline fun <T, U, V, W> Box<T>.alsoMap(f: (Box<U>) -> Box<V>, value: W): Box<W> = Box(value)
