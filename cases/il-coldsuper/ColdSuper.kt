// bir2cir SuspendColdLowering (#78/#90) — a suspend call keyed on a SUBCLASS static receiver resolves against a
// suspend member declared on a SUPERTYPE (the cold entry the subclass inherits). This is the exact shape that
// dropped in the kotlinx port: DeferredCoroutine.await -> JobSupport.awaitInternal (declared on a BASE class) and
// ChannelCoroutine.receiveOrNull -> ReceiveChannel (declared on a super-INTERFACE). Under R1 every super-declared
// suspend member has a virtual cold entry by unconditional declaration, so native VIRTUAL DISPATCH through the cold
// slot resolves the inherited call — no bir2cir hierarchy walk. Also exercises MUTUAL recursion (ping/pong): both
// cold entries exist by construction, so neither caller can be dropped.

suspend fun echo(x: Int): Int = x            // a shared generic-free cold entry (the suspension target)

// --- base-class-declared suspend member, called through a subclass receiver (the JobSupport.awaitInternal shape)
abstract class Base {
    suspend fun awaitInternal(): Int {       // declared on the BASE
        return echo(10)
    }
}
class Derived : Base() {
    suspend fun await(): Int {
        return awaitInternal() + 1           // call site: implicit `this:Derived`, member declared on Base
    }
}

// --- super-interface-declared suspend member, called through an impl receiver (the ReceiveChannel shape)
interface Source {
    suspend fun receiveOrNull(): Int         // declared on the super-INTERFACE (abstract)
}
open class ChannelBase : Source {
    override suspend fun receiveOrNull(): Int = echo(41)
}
class ChannelImpl : ChannelBase() {
    suspend fun consume(): Int = receiveOrNull() + 1   // call site: implicit `this:ChannelImpl`, decl on ChannelBase/Source
}

// --- mutual recursion across a suspension (the transformability set must keep both whole)
suspend fun ping(n: Int): Int { if (n <= 0) return 0; return 1 + pong(n - 1) }
suspend fun pong(n: Int): Int { if (n <= 0) return 0; return 1 + ping(n - 1) }

suspend fun main() {
    println(Derived().await())        // 11
    println(ChannelImpl().consume())  // 42
    println(ping(5))                  // 5
}
