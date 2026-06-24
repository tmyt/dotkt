// .NET unsigned parameters map to Kotlin's unsigned types (System.UInt32 == kotlin.UInt, etc.). Before this,
// facadegen left them as the bare .NET name (`UInt32`), which wouldn't unify with `UInt` — e.g. WinUI's
// `Bootstrap.Initialize(uint)` rejected a `0x...u` argument.
import Boot.Strap
fun main() {
    println(Strap.Initialize(0x00010006u))   // -> 65542  (1.6 packed: major 0x0001, minor 0x0006)
    println(Strap.Big(41uL))                 // -> 42
}
