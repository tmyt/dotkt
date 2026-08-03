@file:OptIn(kotlin.concurrent.atomics.ExperimentalAtomicApi::class)

package roundtrip.atomictwin

import kotlin.concurrent.atomics.AtomicInt as KotlinAtomicInt
import kotlin.concurrent.atomics.AtomicReference as KotlinAtomicReference
import kotlin.reflect.KProperty

class AtomicInt internal constructor(private val atomic: KotlinAtomicInt) {
    var value: Int
        get() = atomic.load()
        set(value) { atomic.store(value) }

    operator fun getValue(thisRef: Any?, property: KProperty<*>): Int = value
    operator fun setValue(thisRef: Any?, property: KProperty<*>, value: Int) { this.value = value }

    fun incrementAndGet(): Int {
        val next = atomic.load() + 1
        atomic.store(next)
        return next
    }
}

class AtomicRef<T> internal constructor(private val atomic: KotlinAtomicReference<T>) {
    var value: T
        get() = atomic.load()
        set(value) { atomic.store(value) }

    fun compareAndSet(expected: T, update: T): Boolean = atomic.compareAndSet(expected, update)
}

fun atomic(initial: Int): AtomicInt = AtomicInt(KotlinAtomicInt(initial))
fun <T> atomic(initial: T): AtomicRef<T> = AtomicRef(KotlinAtomicReference(initial))
