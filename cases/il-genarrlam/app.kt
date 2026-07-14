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
//
// #4 (READ side): reading the erased element back ACROSS the Box<Int>/Box<String> boundary. kotc stamps the
// call `Box<Int>.get_a()` with T already substituted -> `Array<Ref<Nullable(Int)>>`, which lowers to the
// irreconcilable `Ref<Nullable<int32>>` where the member actually returns the erased `Ref<object>` (Ref<object>
// and Ref<Nullable<int32>> are unrelated invariant reified generics — no castclass reconciles them) -> ilverify
// StackUnexpected at the element read / slot store. NullableTvErasureCallRealign re-derives the callsite return
// from the erased declaration and flows the corrected `Ref<object>` receiver through the chained `.v` read.
// Exercised below for BOTH a value-type element (Box<Int>) and a reference element (Box<String>), via a direct
// field-index read, a getter method, a retyped local, and a value-typed consumer (`val x: Int? = …`).
class Ref<X>(val v: X)

fun <X> mk(x: X): Ref<X> = Ref(x)

class Box<T>(size: Int) {
    val a: Array<Ref<T?>> = Array(size) { mk<T?>(null) }
    fun count(): Int = a.size
    fun elem(i: Int): Ref<T?> = a[i]
}

fun main() {
    val b = Box<Int>(2)
    println(b.count())
    println(b.a[0].v)           // value-type element: chained read across the boundary
    println(b.elem(1).v)        // value-type element: via the getter method
    val r: Ref<Int?> = b.a[0]   // value-type element: retyped local (irreconcilable generic arg)
    println(r.v)
    val x: Int? = b.a[1].v      // value-type element: value-typed consumer (object -> Nullable<int32>)
    println(x)
    val s = Box<String>(3)
    println(s.count())
    println(s.a[2].v)           // reference element: chained read across the boundary
    val rs: Ref<String?> = s.elem(0)
    println(rs.v)
}
