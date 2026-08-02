// #86 — the crossing at the IMPLEMENTING position, on a PLAIN .NET interface.
//
// A call is not the only way to meet an uninhabitable slot. A Kotlin class can DERIVE from a .NET type that declares
// one, and there the crossing is in the slot the class must FILL rather than in anything it calls — so the call-side
// refusal never saw it and the class died at load. Emitting the declaration's own signature would not fix it: the
// Kotlin body still reads the argument as Kotlin's `List<object>`, which would move the mismatch out of load time
// and into the body. No valid CIL lowering exists, so the author is owed the message.
import plainnet.ITake
import System.Collections.Generic.List

class CI : ITake {
    override fun Take(xs: List<Int?>): String = "I:ok"
}

fun main() {
    println(CI().toString().substring(0, 2))
}
