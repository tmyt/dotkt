// Minimal kotlinx.atomicfu facade (the real jar isn't on the test classpath; fqNames match so the backend maps
// them to DotKt.Coroutines.Atomic* wrappers exactly as it will for the real library).
package kotlinx.atomicfu

class AtomicInt {
	var value: Int = 0
	fun compareAndSet(expect: Int, update: Int): Boolean = TODO()
	fun incrementAndGet(): Int = TODO()
	fun getAndSet(value: Int): Int = TODO()
	fun addAndGet(delta: Int): Int = TODO()
}
class AtomicRef<T> {
	var value: T = TODO()
	fun compareAndSet(expect: T, update: T): Boolean = TODO()
	fun getAndSet(value: T): T = TODO()
}
fun atomic(initial: Int): AtomicInt = TODO()
fun <T> atomic(initial: T): AtomicRef<T> = TODO()
