// #137 (Avalonia report B): call C#-origin `[Extension]` methods on facadegen-injected .NET types. `import Interop.*`
// brings the namespace's C#-extension methods into scope AS TOP-LEVEL Kotlin extension functions — the Kotlin analog
// of C# `using Interop;`, the enabling seam for Avalonia's fluent startup/render surface (UsePlatformDetect/…). The
// per-extension member-import form (`import Interop.Ext.Twice`, the `using static` analog) is covered by il-c1net.
import Interop.*

fun main() {
    val w = Interop.W(21)
    println(w.Twice())              // top-level extension                          -> 42
    println(w.PlusN(1))             // top-level extension with an arg              -> 22
    val b = Interop.Box<String>("hi")
    println(b.Echo())               // generic extension `fun <T> Box<T>.Echo(): T` -> hi
}
