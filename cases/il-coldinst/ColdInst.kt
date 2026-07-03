// bir2cir SuspendColdLowering P3 wave-2a: INSTANCE suspend members + MEMBER/cross-file suspend
// CALLS. The SM carries a `$this` field for instance members; the cold entry is an instance
// `<name>$dotkt_suspend` on the class. Drained by the synthesized plain `main` (sync completion).

// INST1: a plain instance suspend member (no suspension in its body -> a direct instance cold entry).
class Counter(var n: Int) {
    suspend fun bump(): Int { n += 1; return n }
}

// INST2: an instance member that makes suspend CALLS to another instance member (this.helper()),
// with a local (h) crossing the suspension -> the SM's `$this` field + spilled local field.
class Svc(val base: Int) {
    suspend fun helper(): Int { return base }
    suspend fun chain(): Int {
        val h = helper()
        return h + this.helper()
    }
}

// INSTGEN: instance + generic class (direct member cold entry inheriting the class type param T).
class Box<T>(val v: T) {
    suspend fun get(): T = v
}

// MCALL1: a top-level suspend fun calling a suspend member (obj.bump()).
suspend fun topUse(): Int {
    val c = Counter(100)
    return c.bump()
}

suspend fun main() {
    val c = Counter(10)
    println(c.bump())
    println(c.bump())
    val s = Svc(5)
    println(s.chain())
    val b = Box(42)
    println(b.get())
    val bs = Box("hi")
    println(bs.get())
    println(topUse())
    println(crossFileVal())
}
