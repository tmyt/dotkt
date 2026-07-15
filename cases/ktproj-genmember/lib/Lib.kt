package p2

// #33: a cross-module Kotlin LIBRARY whose public generic types expose members whose DECLARED return is an OPEN type
// variable of the owner (`Pair2<A,B>.a`/`.b` = A/B, `Wrap<X>.items` = List<X>). A DIRECT read of such a member on a
// CONCRETELY-instantiated owner (`Pair2<Int, MutableList<Int>>.b`) surfaces the member's return as the bare `tv` —
// bir2cir's StaticTypeResolver.Surface must substitute it against the receiver's concrete instantiation so downstream
// recognizers (the println collection-wrap) key on the real type, not the open tv. Regression guard for #33.

class Pair2<A, B>(val a: A, val b: B)
class Wrap<X>(val items: List<X>)

fun <A, B> pair2(a: A, b: B): Pair2<A, B> = Pair2(a, b)
fun <X> wrap(items: List<X>): Wrap<X> = Wrap(items)
