// #144: same-name/same-arity C# extension methods on DIFFERENT receiver types (plain class / primitive), in ONE
// namespace, must each bind to their OWN receiver's static class (keyed by the receiver's classifier ClassId), not the
// arbitrary-first candidate. `bar.Tag()` / `5.Kind()` would mis-bind to Foo / String without receiver disambiguation.
import Interop.*

fun main() {
    val foo = Interop.Foo(10)
    val bar = Interop.Bar(20)
    println(foo.Tag())          // FooExt.Tag  -> 11
    println(bar.Tag())          // BarExt.Tag  -> 120
    println(foo.Mix(3))         // FooExt.Mix  -> 30
    println(bar.Mix(5))         // BarExt.Mix  -> 15
    println("abcd".Kind())      // StrExt.Kind (this string) -> 4    (primitive receiver, vocabulary-divergent)
    println(7.Kind())           // IntExt.Kind (this int)    -> 1007 (primitive receiver, vocabulary-divergent)
}
