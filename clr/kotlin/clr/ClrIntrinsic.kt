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

// Bitwise-combinable ACCESS flags for @ClrProperty. `READ` = a get accessor, `WRITE` = a set accessor; `READ or WRITE`
// (const-foldable) marks a get+set property. Int (not enum/Boolean) because an Int primitive attr arg encodes into the
// ref.dll reliably (an enum arg may not encode via ilemit), and `const val` inlines the literal at the use site.
public const val READ: Int = 1
public const val WRITE: Int = 2

// Explicitly binds a Kotlin property OR a standalone accessor FUNCTION to a .NET PROPERTY `name`: bir2cir reads it from
// the REFERENCE assembly (NOT kotc) and routes reads -> clrPropGet(name) [access has READ], writes -> clrPropSet(name)
// [access has WRITE] — the accessor role stated EXPLICITLY, replacing the fragile get_/set_ intrinsic STRING-PREFIX
// sniff. For the Kotlin idiom where a property's read/write is split across a read-only `val X` + a standalone
// `fun setX(v)` (e.g. StringBuilder.length + setLength()), each accessor carries @ClrProperty with the SAME `name`.
// Distinct from @ClrIntrinsic (which binds to a like-named .NET METHOD). Indexers (get_Item(i)/set_Item(i,v) — they take
// an index arg) are genuine methods and STAY @ClrIntrinsic. No default arg on `access` (cross-module default-arg values
// are dropped by the frontend jar); always pass both `access` and `name`.
@Target(AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
public annotation class ClrProperty(val access: Int, val name: String)

// Like @ClrIntrinsic on a MEMBER, but the member binds to the named .NET member DYNAMICALLY: a CALL to it is emitted as
// a runtime reflective dispatch instead of a static method reference. Slower, but it sidesteps static resolution that
// otherwise cascades -- e.g. a Kotlin abstract collection (AbstractMutableList.SubList) calling get_Item where the
// interface is a BCL `clrg:IList` (which ilemit's static FindMethod skips), or the IReadOnlyList/IList get_Item dual
// slot. Use ONLY where static @ClrIntrinsic cannot be resolved; the implementation side stays static (covariant bridge).
@Target(AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
public annotation class ClrIntrinsicAsDynamic(val name: String)
