// The @ClrIntrinsic binding annotation (the kotlin.clr-namespaced replacement for the now-removed legacy root-package binding): a MEMBER
// (function / property) of a CLR-bound class binds to the named .NET member. CLASS-level type aliasing is now
// @ClrTypeAlias's role — @ClrIntrinsic NO LONGER targets CLASS (the @Target below enforces the role split, so
// @ClrIntrinsic on a class is a compile error). bir2cir reads it from the REFERENCE assembly (NOT kotc) and
// substitutes the member call:
//  - on a MEMBER -> binds to the named .NET member (e.g. @ClrIntrinsic("Length") on a member of a
//    @ClrTypeAlias("System.String") class -> System.String.get_Length); an unannotated member rolls up to its own name.
//  - on a TOP-LEVEL fun -> a STATIC .NET method, splitting "Namespace.Type.Method" at the last '.'.
package kotlin.clr

// CLR owner provenance attached by dll2klib to a projected CLR class or top-level declaration. bir2cir remains the
// sole layer that interprets this owner and chooses a physical call/property/field/operator shape. Exact Kotlin
// declaration identity is a separate compiler fact carried by KotlinDeclarationIdentity below.
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
@Retention(AnnotationRetention.BINARY)
public annotation class ClrExternal(val owner: String)

// Marks a metadata-only suspend declaration synthesized by dll2klib beside a
// conforming CLR GetAwaiter pattern. kotc forwards this declaration fact to
// BIR; bir2cir resolves GetAwaiter/GetResult and realizes the suspension.
// There is deliberately no CLR method with this name to bind or emit.
@Target(AnnotationTarget.FUNCTION)
@Retention(AnnotationRetention.BINARY)
public annotation class ClrAwaitBridge

// A projected CLR FieldDef has no exact declaration form in Kotlin metadata:
// KLIB exposes it as a Kotlin property for frontend lookup, while this marker
// preserves the storage fact so kotc emits a BIR field access. bir2cir still
// resolves the actual CLR field and ilemit only emits the resulting CIR.
@Target(AnnotationTarget.PROPERTY)
@Retention(AnnotationRetention.BINARY)
public annotation class ClrField

// Kotlin 2.4 does not copy KLIB IS_LATEINIT onto its synthetic static-property fake override. dll2klib therefore
// carries the declaration fact in a compiler-only marker alongside ClrField. It is intentionally not a public flag
// on the user-facing interop annotation: arbitrary @ClrField source must not be able to manufacture lateinit reads.
@Target(AnnotationTarget.PROPERTY)
@Retention(AnnotationRetention.BINARY)
internal annotation class ClrLateinitField

// Compiler-internal DLL -> KLIB linkage for members. Properties carry getter and setter identities separately.
@Target(AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
@Retention(AnnotationRetention.BINARY)
public annotation class KotlinDeclarationIdentity(val id: String, val setterId: String)

@Target(AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
public annotation class ClrIntrinsic(val name: String)

// Marks a primitive numeric CONVERSION function (`Int.toLong()`, `Double.toInt()`, `Char.toInt()`, ...): a call to it
// lowers to a CIL `conv` to the function's OWN declared return type (toLong -> kotlin.Long, toInt -> kotlin.Int, ...).
// bir2cir reads this marker off the REFERENCE assembly and emits `{k:conv, to:<callee return type>, e:<receiver>}` — the
// SAME node kotc used to synthesize from a `NUMBER_CONV[name]` name-heuristic. The genuine primitive IL op stays a
// lowering (ilemit selects conv.i4/conv.i8/conv.r8), but the RECOGNITION ("this call is a numeric conversion") is now
// metadata on the exact stdlib symbol, not a kotc name+receiver guess (which could misfire on any `toLong`-named member
// with a numeric receiver). NO argument: the conv target is always the callee's declared return type.
@Target(AnnotationTarget.FUNCTION)
public annotation class ClrConv

// NOTE: the collection/array FACTORY markers (@ClrCollectionFactory / @ClrArrayFactory) are defined in the COMMON source
// set (libraries/stdlib/src/kotlin/clr/Factories.kt), NOT here. They must annotate the vararg factory bodies that live in
// the COMMON stdlib (`listOf`/`mapOf` in kotlin.collections, the unsigned array factories) — and a COMMON source cannot
// reference a PLATFORM-only declaration under the jar's multi-platform compile. So the two factory annotations live in
// common (where common + platform sources can both see them); the other kotlin.clr bindings above stay platform-only.

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
// are dropped by the frontend KLIB); always pass both `access` and `name`.
@Target(AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
public annotation class ClrProperty(val access: Int, val name: String)

// Marks a PLAIN-typed stdlib method parameter as passed BY REFERENCE (`ref`/`out`) to the bound BCL member. Kotlin has
// no `ref`/`out` syntax, so the byref-ness is carried as binding METADATA on a normal parameter (a plain `Int`, NOT a
// CLR-interop `ClrRef<T>`): bir2cir reads it from the REFERENCE assembly and wraps that argument position `byref:` in
// the @ClrIntrinsic-substituted call, so ilemit resolves the `ref`/`out` overload and emits the address-load
// (ldloca for a local, ldflda for a field). This keeps the CLR stdlib ABI identical to the standard (JVM) Kotlin
// stdlib — the visible signature is unchanged — so user code stays source/ABI-compatible; `ClrRef<T>`/`byref` stay
// USER-code CLR-interop intrinsics only and never appear in stdlib source. A marker (no args): the position is the
// only datum. (kotc also reads it, but ONLY to shape the argument as an addressable lvalue — the backing FIELD of a
// property read rather than its getter call; the CLR call-substitution decision itself is bir2cir's.)
@Target(AnnotationTarget.VALUE_PARAMETER)
public annotation class ClrRefArgument

// Marks an exclusive-end parameter of an @ClrIntrinsic binding whose CLR target takes a count in the same slot.
// `startIndex` names the zero-based position in the Kotlin declaration's ordinary parameter list (the receiver is not
// counted) whose value must be subtracted from this one. bir2cir preserves Kotlin's receiver/argument evaluation order
// and evaluates both indices once while rewriting end -> end - start. Internal: this is compiler-provided stdlib
// binding vocabulary, not a user-facing interop annotation.
@Target(AnnotationTarget.VALUE_PARAMETER)
internal annotation class ClrCountFromExclusiveEnd(val startIndex: Int)

// Carries a parameter's DEFAULT-VALUE expression as embedded BIR so a CROSS-MODULE caller that OMITS the argument can
// have it filled. The frontend KLIB drops a callee's default VALUES (hands them back as IrErrorExpression), and .NET
// `[DefaultParameterValue]` metadata can only carry a CONSTANT of the parameter's own type — it cannot represent a
// non-null object/`CharSequence` default (e.g. `joinToString`'s `prefix: CharSequence = ""`, which is 4-A-coerced to
// `new <>dotkt_StringCharSequence("")`, a non-constant). So kotc STAMPS this on the defaulted parameter when compiling
// the CALLEE (the stdlib), where the default expression IS available in the IR — `index` = the parameter's position in
// the emitted call (extension-receiver-inclusive), `bir` = the default expression as a BIR-json string. bir2cir READS
// it from the REFERENCE assembly and SPLICES the BIR as the omitted argument (before StringCharSequenceBridge +
// BirTypeLowering, so a String default is coerced to CharSequence exactly like an explicit argument), mirroring the
// [KotlinInline] body-splice mechanism. Constant defaults keep riding `[DefaultParameterValue]` (unchanged); this rides
// the ref.dll only (param attrs are stripped in the runtime build — exactly bir2cir's read surface).
@Target(AnnotationTarget.VALUE_PARAMETER)
public annotation class KotlinDefault(val index: Int, val bir: String)
