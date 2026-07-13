// #142: `Array(size){ mk<T?>(null) }` INSIDE a generic class. The lambda's factory returns `Ref<T?>`, a
// CONSTRUCTED-generic whose arg is a nullable type-var. bir2cir erases `Nullable(Tv)` to `object` in the
// method-return / array-elem positions, so the `__lambda0` factory signature is `Ref<object>` and the array
// element is `Ref<object>[]`. The `newDelegate.funcType.ret` must agree — but the old EraseNullableTv `Fn`
// arm passed `fn.Ret` VERBATIM (a carve-out meant only for a TOP-LEVEL `(...)->T?` hand-off to
// NullableFuncReturnErasure), so `Ref<Nullable(Tv)>` survived there ONLY, then ReferenceNullableStrip stripped
// it to the bare `Ref<!T>` — an internally-contradictory funcType.ret vs the `Ref<object>` ldftn target →
// ilverify `[DelegateCtor] Box`1::.ctor Unrecognized arguments`. The narrowed carve-out (only a top-level
// `Nullable(Tv)` return stays verbatim) erases the nested `Ref<T?>` to `Ref<object>` consistently, funcType /
// method-signature / array-elem agree, DelegateCtor is gone.
class Ref<X>(val v: X)

fun <X> mk(x: X): Ref<X> = Ref(x)

class Box<T>(size: Int) {
    val a: Array<Ref<T?>> = Array(size) { mk<T?>(null) }
    fun count(): Int = a.size
}

fun main() {
    val b = Box<Int>(2)
    println(b.count())
    val s = Box<String>(3)
    println(s.count())
}
