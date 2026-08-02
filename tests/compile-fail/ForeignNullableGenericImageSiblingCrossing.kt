// #86 — the crossing override whose ERASED IMAGE is a sibling slot's real signature.
//
// `Take(xs: List<Int?>)` is the `List<int?>` slot's override and physically states `Take(List<object>)`, which the
// base also declares for real. Deciding ownership physically let the sibling discharge the crossing: this compiled,
// and the CLR bound the emitted body to the `object` slot while a call through `Take(List<int?>)` ran the base's
// own implementation — the silent wrong answer this refusal exists to prevent.
//
// Which source slot a body belongs to is answered by the fact the erasure recorded on the declaration: this
// parameter carries its pre-erasure `List<Int?>` on `[KotlinNullableGeneric]`, and the sibling override of
// `List<Any?>` — accepted, and driven in tests/interop — carries nothing there.
import plainnet.ImageSiblingBase
import System.Collections.Generic.List

class CImageSibling : ImageSiblingBase() {
    override fun Take(xs: List<Int?>): String = "kt-q"
}

fun main() {
    println(CImageSibling().toString().substring(0, 2))
}
