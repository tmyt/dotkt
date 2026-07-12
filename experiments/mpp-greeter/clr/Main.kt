package mpp.greeter

// Entry point (platform source). Uses the common `Greeter` type, whose `say()` is resolved through
// the expect/actual match to the CLR actual above.
fun main() {
    println(Greeter().say())
}
