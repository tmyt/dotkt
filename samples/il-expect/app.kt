// Phase 3 — (a) expect/actual compiled as common+platform fragments (one invocation, -Xcommon-sources), and
// (b) kotlinx.atomicfu mapped to Interlocked/Volatile wrappers.
import kotlinx.atomicfu.atomic

fun main() {
    println(platformName())          // CLR  (actual)
    println(answer())                // 42   (actual)
    println(commonGreeting())        // hello from CLR

    val counter = atomic(0)
    counter.incrementAndGet()
    counter.incrementAndGet()
    println(counter.value)           // 2
    println(counter.compareAndSet(2, 10))  // true
    println(counter.value)           // 10
    println(counter.addAndGet(5))    // 15

    val ref = atomic<String?>(null)
    println(ref.compareAndSet(null, "hi"))  // true
    println(ref.value)               // hi
}

fun commonGreeting(): String = "hello from " + platformName()
