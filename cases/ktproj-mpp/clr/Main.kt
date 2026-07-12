package mpp.app

// Entry point (platform fragment). Uses the common `Greeter`, whose `say()` resolves through the
// expect/actual match to the CLR actual.
fun main() {
    println(Greeter().say())
}
