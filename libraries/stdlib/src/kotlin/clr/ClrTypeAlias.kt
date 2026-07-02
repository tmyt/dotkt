// @ClrTypeAlias marks a CLASS as a type alias to a .NET type: the Kotlin declaration *is* the named .NET type, so it is
// substituted away (NOT emitted) in the runtime/app assemblies and every use of it resolves to that .NET type. This is
// the TYPE-substitute role, split out of @ClrIntrinsic so the two concerns are separate:
//   - @ClrTypeAlias  on a CLASS   -> type-identity substitute  (kotlin.Int -> System.Int32, Iterable -> IEnumerable<T>)
//   - @ClrIntrinsic  on a MEMBER  -> call substitute           (List.size -> get_Count, String.format -> String.Format)
// kotc recognizes it by the FQN `kotlin.clr.ClrTypeAlias`. Like @ClrIntrinsic it is gated: the REFERENCE assembly
// (DOTKT_STDLIB_COMPILE without SUBSTITUTE) keeps the class AND the attribute for round-trip metadata; the runtime/app
// assemblies substitute it away. Primitives (Int/Long/Byte/Short/Float/Double/Char/Boolean) carry it so the runtime
// typealias-strip is annotation-driven instead of a hard-coded compiler list.
//
// This marker lives in a COMMON source set (not the platform `clr/` set) on purpose: the unsigned value classes
// (UInt/UByte/UShort/ULong) are declared in common (`unsigned/src`, no platform actual) yet must carry @ClrTypeAlias to
// be substituted to System.UInt32/Byte/UInt16/UInt64. A platform-only declaration is invisible to common sources, so the
// annotation must be common; platform code (the primitive `actual`s in clr/builtins) still sees it (platform ⊇ common).
package kotlin.clr

@Target(AnnotationTarget.CLASS)
public annotation class ClrTypeAlias(val name: String)
