// #155 (audit extension, Fable-flagged): a bound reference to a PRIVATE method (`this::secret`) evaluated INSIDE a
// lambda that is lifted to a SEPARATE closure class capturing `this`. The closure body emits
// newBoundDelegate{ownerType: Box, method: secret, recv: __outer} — an `ldftn` over Box's PRIVATE method from a
// DIFFERENT top-level CLR class -> MethodAccessException at runtime, the SAME cross-class-private fault class as the
// lateinit field case (il-lateinitrefpriv). bir2cir CrossClassPrivateWidening must widen newBoundDelegate too.
class Box {
    private fun secret(): String = "secret"
    fun deferred(): () -> String {
        val make: () -> (() -> String) = { this::secret }   // this::secret lives in a lifted closure over `this`
        return make()
    }
}

fun main() {
    val b = Box()
    println(b.deferred()())      // secret
}
