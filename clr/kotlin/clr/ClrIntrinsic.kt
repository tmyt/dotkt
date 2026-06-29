// The @ClrIntrinsic binding annotation (the kotlin.clr-namespaced successor to the legacy `clr.Clr`), available to the
// stdlib's CLR actuals so they can bind to BCL types/members. kotc recognizes it by the FQN `kotlin.clr.ClrIntrinsic`
// (and, for backward compatibility, still the legacy `clr.Clr`):
//  - on a CLASS  -> the declaration resolves to the named .NET type (e.g. @ClrIntrinsic("System.Text.StringBuilder")).
//  - on a MEMBER -> the member binds to the named .NET member; an unannotated member rolls up to its own name.
//  - on a TOP-LEVEL fun -> binds to a STATIC .NET method, splitting "Namespace.Type.Method" at the last '.'.
package kotlin.clr

@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
public annotation class ClrIntrinsic(val name: String)

// Like @ClrIntrinsic on a MEMBER, but the member binds to the named .NET member DYNAMICALLY: a CALL to it is emitted as
// a runtime reflective dispatch instead of a static method reference. Slower, but it sidesteps static resolution that
// otherwise cascades -- e.g. a Kotlin abstract collection (AbstractMutableList.SubList) calling get_Item where the
// interface is a BCL `clrg:IList` (which ilemit's static FindMethod skips), or the IReadOnlyList/IList get_Item dual
// slot. Use ONLY where static @ClrIntrinsic cannot be resolved; the implementation side stays static (covariant bridge).
@Target(AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
public annotation class ClrIntrinsicAsDynamic(val name: String)
