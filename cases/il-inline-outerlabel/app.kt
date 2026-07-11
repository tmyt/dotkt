// #75 S4a §8.2 — f outer@ { g { return@outer } }: `return@outer` targets the `run` lambda La (the seed of La's
// scan) -> LOCAL for `run` -> `run` takes the DELEGATE path. When La compiles as the closure invoke, the nested
// forEach is re-gated: return@outer now ESCAPES forEach's lambda -> forEach splices INTO La's invoke; the return
// becomes the invoke's own return. Post-label ("after") runs. Correct at both levels.
fun main() {
    run outer@{
        listOf(1, 2, 3).forEach {
            if (it == 2) return@outer
            println(it)
        }
        println("unreached")
    }
    println("after")
}
