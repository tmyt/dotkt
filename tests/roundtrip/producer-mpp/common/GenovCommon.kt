// ktproj-genov-common (#25 RESIDUAL) — COMMON fragment of the multiplatform library. A GENERIC top-level factory
// declared in a `common/` FILE named GenovCommon.kt, so its emitted .NET file class is `GenovCommonKt` (the
// `*CommonKt` shape), with a real body. The reduced kotlinx-atomicfu `atomicArrayOfNulls<T>(size): AtomicArray<T?>`
// shape: a sole-generic factory living in the COMMON fragment.
//
// kotc emits the cross-module generic call as `callStatic ownerType=kotlinx.genovc.GenovCommonKt typeArgs=[…]
// shapeTypes=[…]` with NO concrete `sig`. bir2cir must promote `shapeTypes`->`sig` so ilemit binds the overload by
// sig then MakeGenericMethod. The residual: bir2cir's owner-attribution recovers an EMPTY receiver-key from a sig-less
// generic call, so when the bare fun name is present under MORE THAN ONE file-class owner in the ref index (here
// `arrOfNulls` also exists in the sibling package's `GenovAltKt`, clr/GenovAlt.kt), the ref index can NOT disambiguate
// the owner -> the promotion was skipped -> ilemit reported "static method not found". bir2cir now adopts kotc's
// facadegen-injected `ownerType` as the owner and promotes even when the ref index can't attribute it.
package kotlinx.genovc

// `Arr<T>` DELIBERATELY shares its simple name with `kotlinx.genov.Arr<T>` in the single-platform producer's OTHER dll
// — the #199-③ regression: facadegen must emit `arrOfNulls`'s return as the NAMESPACE-QUALIFIED `kotlinx.genovc.Arr`,
// so the injector binds `arrOfNulls<String>(3)` to THIS dll's `Arr`, not the twin (a bare name last-wins, producing a
// cross-assembly type mismatch ilverify flags as StackUnexpected). Also tests the common-fragment promotion (#25 residual).
class Arr<T>(val size: Int)

fun <T> arrOfNulls(n: Int): Arr<T> = Arr(n)
