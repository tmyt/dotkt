// The @ClrIntrinsic binding annotation (the kotlin.clr-namespaced replacement for the now-removed legacy root-package binding): a MEMBER
// (function / property) of a CLR-bound class binds to the named .NET member. CLASS-level type aliasing is now
// @ClrTypeAlias's role — @ClrIntrinsic NO LONGER targets CLASS (the @Target below enforces the role split, so
// @ClrIntrinsic on a class is a compile error). bir2cir reads it from the REFERENCE assembly (NOT kotc) and
// substitutes the member call:
//  - on a MEMBER -> binds to the named .NET member (e.g. @ClrIntrinsic("Length") on a member of a
//    @ClrTypeAlias("System.String") class -> System.String.get_Length); an unannotated member rolls up to its own name.
//  - on a TOP-LEVEL fun -> a STATIC .NET method, splitting "Namespace.Type.Method" at the last '.'.
package kotlin.clr

@Target(AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
public annotation class ClrIntrinsic(val name: String)

// Like @ClrIntrinsic on a MEMBER, but the member binds to the named .NET member DYNAMICALLY: a CALL to it is emitted as
// a runtime reflective dispatch instead of a static method reference. Slower, but it sidesteps static resolution that
// otherwise cascades -- e.g. a Kotlin abstract collection (AbstractMutableList.SubList) calling get_Item where the
// interface is a BCL `clrg:IList` (which ilemit's static FindMethod skips), or the IReadOnlyList/IList get_Item dual
// slot. Use ONLY where static @ClrIntrinsic cannot be resolved; the implementation side stays static (covariant bridge).
@Target(AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
public annotation class ClrIntrinsicAsDynamic(val name: String)
