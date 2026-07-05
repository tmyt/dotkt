import kotlin.concurrent.Volatile

// `@Volatile` on a value-type backing field and on a reference-type backing field. On the CLR these emit a
// `modreq(IsVolatile)` field (the C# `volatile` shape) + a `volatile.` prefix on every backing-field ld/st.
// The single-threaded gate proves FUNCTIONAL correctness (reads observe writes); the MEMORY-VISIBILITY guarantee
// rests on the modreq being exactly the C# volatile encoding, which the JIT honors (not observable single-threaded).
class Counter {
    @Volatile var value: Int = 0        // value-type volatile field
    @Volatile var label: String? = null // reference-type volatile field

    fun bump() { value = value + 1 }    // read + write through the volatile backing field
}

// A top-level `@Volatile var` -> a volatile STATIC field (Ldsfld/Stsfld path).
@Volatile var globalFlag: Boolean = false

fun main() {
    val c = Counter()
    println(c.value)      // 0
    c.value = 41
    println(c.value)      // 41
    c.bump()
    println(c.value)      // 42
    c.label = "ready"
    println(c.label)      // ready

    globalFlag = true
    println(globalFlag)   // True
}
