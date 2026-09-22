package inlinedefaults

inline fun <T> through(block: () -> T): T = block()

class Receiver {
    var field = 1
    var trace = ""
    fun mark(value: String) { trace += value }
    fun next(value: Int = run { field += 1; field }): Int = value
    fun nested(value: Int = through { next() }): Int = value
    fun ordered(
        first: Int = run { mark("D"); 10 },
        supplied: Int,
        second: Int = run { mark("E"); first + supplied }
    ): Int = first + supplied + second
}

class GenericReceiver<T>(var field: T) {
    fun assign(replacement: T, value: T = run { field = replacement; field }): T = value
}

fun callback(block: () -> Int = { run { 41 } }): Int = block()
fun String.receiverLength(value: Int = run { length }): Int = value
open class Base(val value: Int = run { 42 })

inline fun capture(value: Int, before: () -> Unit): () -> Int {
    before()
    return { value }
}

class CaptureDefault {
    fun read(value: () -> Int = capture(257) {}): Int = value()
}

class CompanionDefault {
    companion object { inline fun <T> wrap(block: () -> T): T = block() }
    fun read(value: Int = wrap { 7 }): Int = value
}
