// Multi-file lifted lambdas: each file lifts its own `__lambdaN` into its file class. One BirEmitter instance
// processes all files, so its per-file lifted state must reset per file — otherwise file A's lambdas leak into
// file B's class (duplicated into every file class, as seen building a WinUI app).
fun runA(f: () -> Unit) { f() }
fun fromA() { runA { println("A1") }; runA { println("A2") } }
