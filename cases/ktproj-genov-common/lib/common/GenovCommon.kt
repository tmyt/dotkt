// #25 RESIDUAL — COMMON fragment of a multiplatform library. A GENERIC top-level factory declared in a `common/`
// source FILE named GenovCommon.kt, so its emitted .NET file class is `GenovCommonKt` (the `*CommonKt` shape), with
// a real body. This is the reduced kotlinx-atomicfu `atomicArrayOfNulls<T>(size): AtomicArray<T?>` shape: a
// sole-generic factory that lives in the COMMON fragment.
//
// kotc emits the cross-module generic call as `callStatic ownerType=kotlinx.genovc.GenovCommonKt typeArgs=[…]
// shapeTypes=[…]` with NO concrete `sig` (the pure-Kotlin overload SHAPE). bir2cir must promote `shapeTypes`->`sig`
// so ilemit binds the overload by sig then MakeGenericMethod. The residual: bir2cir's owner-attribution recovers an
// EMPTY receiver-key from a sig-less generic call, so when the bare fun name is present under MORE THAN ONE
// file-class owner in the ref index (here `arrOfNulls` also exists in the sibling package's `GenovAltKt`), the ref
// index can NOT disambiguate the owner -> the promotion was skipped -> ilemit reported "static method not found".
package kotlinx.genovc

class Arr<T>(val size: Int)

fun <T> arrOfNulls(n: Int): Arr<T> = Arr(n)
