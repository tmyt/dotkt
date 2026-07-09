package kotc.backend

// Kotlin -> .NET name/shape mapping tables used across the BIR emitter (operators, kotlin.math / kotlin.text /
// collection ops -> their .NET equivalents, primitive/exception type maps). Lifted out of BirEmitter so the
// expr/stmt extension files can reach them by simple name; they are pure data, no emitter state.

// No COMPARE name->symbol map here. The `<`/`<=`/`>`/`>=` desugarings are `kotlin.internal.ir` COMPILER
// INTRINSICS (top-level `less`/`lessOrEqual`/`greater`/`greaterOrEqual`, no ref.dll symbol); like EQEQ/EQEQEQ and
// the ARITHMETIC/BITWISE/UNARY operators, their recognition is bir2cir's. kotc emits the FAITHFUL intrinsic call
// (`callStatic owner=kotlin.internal.ir method=less`, collision-safe vs a user top-level `less`) and bir2cir's
// PrimitiveOperatorLowering re-emits the `binOp` with the operand shaping (primitive gating, nullable-primitive
// unwrap, boxed-Any -> concrete cast).

// No kotlin.math.* -> System.Math.* map here: that CLR knowledge lives in bir2cir. kotc emits a plain call to the
// stdlib math fun; bir2cir substitutes it from MathClr.kt's @ClrIntrinsic bindings on the ref.dll (System.Math.*
// for Double/Int/Long, System.MathF.* for Float).

// No `kotlin.text` String-op -> System.String member-name map (STRING_OPS) here: those ops run as pure-Kotlin stdlib
// bodies (or bir2cir substitution off the ref.dll) — no BCL member name in kotc.
// Only `reversed` STAYS kotc-lowered (strReversed) pending a `StringBuilder(CharSequence)`-ctor stdlib fix (a separate node,
// never in this map).

// No Char-ops map (isDigit/isLetter/uppercaseChar/… -> System.Char statics) here: that CLR knowledge lives in bir2cir.
// kotc emits a plain call to the stdlib Char fun; bir2cir substitutes it from
// CharClr.kt's @ClrIntrinsic("System.Char.IsDigit"/"System.Char.ToUpperInvariant"/…) FQ bindings on the ref.dll.

// No SIGNED-primitive-array recognition SET here: kotc reads array-ness from the IR type system
// (`isPrimitiveArray()`) and emits the type's OWN faithful FQN identity — no kotlin.* array table. bir2cir
// DECOMPOSES the identity to `Array(elem)` + DERIVES the intrinsic `elem` + the sized-ctor construction.
// The UNSIGNED specialized arrays (#53). Unlike the signed arrays above (Kotlin builtins with no source body), these
// are library value classes (`UByteArray(storage: ByteArray)`) — so this native-array lowering applies ONLY in app/rt
// consumer builds (`!stdlibCompile`); the stdlib's OWN compile keeps them as the emitted value class so UByteArray.kt /
// _UArrays.kt compile against a real `storage`. Element = the unsigned scalar (bir2cir lowers kotlin.UByte -> `ubyte`,
// ilemit -> System.Byte[]). Mirrors how kotlin.UInt stays a value class in the ref build but lowers to native `uint` elsewhere.
internal val UNSIGNED_ARRAY_ELEM = mapOf(
	"kotlin.UByteArray" to "kotlin.UByte", "kotlin.UShortArray" to "kotlin.UShort",
	"kotlin.UIntArray" to "kotlin.UInt", "kotlin.ULongArray" to "kotlin.ULong",
)
// There is no collection/array factory RECOGNITION name-set (listOf/setOf/mapOf/arrayOf/intArrayOf/…) here:
// kotc emits the plain top-level factory call (the faithful IR); bir2cir reads the `@kotlin.clr.ClrCollectionFactory`/
// `@kotlin.clr.ClrArrayFactory` marker off each stdlib factory function on the ref.dll and emits the
// newList/newSet/newMap/newArray/newArraySized construction node. A name-set match here would be a kotc CLR-shape
// decision on specific stdlib symbols — exactly the recognition that belongs in bir2cir.
// Int-range/-progression types whose for-loop can be counter-lowered (over get_first/get_last/get_step) when the source
// is a range VALUE (e.g. `for (i in indices)`), avoiding the iterator protocol + its covariant-return iterator.
internal val INT_PROGRESSION_FQ = setOf("kotlin.ranges.IntRange", "kotlin.ranges.IntProgression")

// Top-level reified enum intrinsics, lowered at the call site to the same BIR nodes as `T.values()`/`T.valueOf()`
// (all type args are reified on the CLR). See the interception block in BirEmitter's call lowering.
internal val ENUM_REIFIED_INTRINSICS = setOf(
	"kotlin.enumValues", "kotlin.enumValueOf", "kotlin.enums.enumEntries", "kotlin.enums.enumEntriesIntrinsic",
)

// Numeric conversions (`3.7.toInt()`, `x.toLong()`, `c.toInt()`) are not recognized in kotc: kotc emits the plain
// `callInstance kotlin.Double.toInt` (the faithful IR). bir2cir reads the `@kotlin.clr.ClrConv` marker off each stdlib
// primitive's conversion member on the ref.dll and emits the `conv` node from the callee's return type. A name->target
// map + receiver-type guard here would be a kotc name-heuristic; the `conv` node itself — a genuine primitive IL op —
// is bir2cir-produced, and ilemit selects the conv opcode.

// No value-type-primitive FQN sets here (was PRIMITIVE_EQ_FQ / PRIMITIVE_OP_FQ): "is this a value-type primitive"
// (for the `T?`→Nullable<T> element / the raw-CIL operator + value-coercion gate) is read from the IR type system
// via `IrType.isValuePrimitive()` (= isPrimitiveType) / `isPrimitiveOrUnsigned()` (= isPrimitiveType||isUnsignedType)
// in BirEmitter — the frontend already knows, so kotc does NOT re-hardcode the kotlin.* list.

// No kotlin.* -> System.* exception map here: that CLR knowledge belongs in bir2cir. The
// stdlib's exception classes carry `@kotlin.clr.ClrTypeAlias("System.X")`, and bir2cir reads that off the ref.dll
// to lower throw/catch/supertype/construction (the same @ClrTypeAlias path that lowers the collections). kotc emits
// the bare `kotlin.*Exception` FQN and nothing more. See MEMORY `exception-map-to-clrtypealias`.
