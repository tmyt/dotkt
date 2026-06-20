// Phase 5 — reverse interop: this Kotlin library is compiled to a NORMAL .NET assembly by the IL backend
// (BIR -> ilemit), then consumed from C# via a plain assembly <Reference> (Program.cs). Proves the IL output
// is a first-class .NET assembly, not just a runnable exe.
class Greeter(val name: String) {
    fun greet(): String = "Hi, " + name
}

fun add(a: Int, b: Int): Int = a + b
