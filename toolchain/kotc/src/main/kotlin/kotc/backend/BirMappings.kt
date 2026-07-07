package kotc.backend

// Kotlin -> .NET name/shape mapping tables used across the BIR emitter (operators, kotlin.math / kotlin.text /
// collection ops -> their .NET equivalents, primitive/exception type maps). Lifted out of BirEmitter so the
// expr/stmt extension files can reach them by simple name; they are pure data, no emitter state.

internal val BINARY = mapOf(
	"plus" to "+", "minus" to "-", "times" to "*", "div" to "/", "rem" to "%",
	"less" to "<", "lessOrEqual" to "<=", "greater" to ">", "greaterOrEqual" to ">=",
	"EQEQ" to "==", "EQEQEQ" to "==",
	// Bitwise / shift infix functions (Int/Long/Boolean).
	"and" to "&", "or" to "|", "xor" to "^", "shl" to "<<", "shr" to ">>", "ushr" to ">>>",
)
internal val UNARY = mapOf("unaryMinus" to "-", "unaryPlus" to "+", "not" to "!", "inv" to "~")

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

// Kotlin FQN identity of a primitive array's element (kotc emits the Kotlin FQN; bir2cir lowers to the CLR
// primitive, ilemit picks the opcode). NO `int`/`long` shorthand — that CLR-resolution vocabulary is gone.
internal val PRIMITIVE_ARRAY_ELEM = mapOf(
	"kotlin.IntArray" to "kotlin.Int", "kotlin.LongArray" to "kotlin.Long", "kotlin.DoubleArray" to "kotlin.Double",
	"kotlin.FloatArray" to "kotlin.Float", "kotlin.BooleanArray" to "kotlin.Boolean", "kotlin.CharArray" to "kotlin.Char",
	"kotlin.ByteArray" to "kotlin.Byte", "kotlin.ShortArray" to "kotlin.Short",
)
// The UNSIGNED specialized arrays (#53). Unlike the signed arrays above (Kotlin builtins with no source body), these
// are library value classes (`UByteArray(storage: ByteArray)`) — so this native-array lowering applies ONLY in app/rt
// consumer builds (`!stdlibCompile`); the stdlib's OWN compile keeps them as the emitted value class so UByteArray.kt /
// _UArrays.kt compile against a real `storage`. Element = the unsigned scalar (bir2cir lowers kotlin.UByte -> `ubyte`,
// ilemit -> System.Byte[]). Mirrors how kotlin.UInt stays a value class in the ref build but lowers to native `uint` elsewhere.
internal val UNSIGNED_ARRAY_ELEM = mapOf(
	"kotlin.UByteArray" to "kotlin.UByte", "kotlin.UShortArray" to "kotlin.UShort",
	"kotlin.UIntArray" to "kotlin.UInt", "kotlin.ULongArray" to "kotlin.ULong",
)
internal val ARRAY_FACTORY_NAMES = setOf(
	"arrayOf", "intArrayOf", "longArrayOf", "doubleArrayOf",
	"floatArrayOf", "booleanArrayOf", "charArrayOf", "byteArrayOf", "shortArrayOf",
	// Unsigned specialized-array factories (#53): `ubyteArrayOf(...)` -> a native `new byte[]{...}` (elem `ubyte`).
	"ubyteArrayOf", "ushortArrayOf", "uintArrayOf", "ulongArrayOf",
)
internal val LIST_FACTORIES = setOf(
	"kotlin.collections.listOf", "kotlin.collections.mutableListOf", "kotlin.collections.arrayListOf",
	"kotlin.collections.emptyList",
)
internal val SET_FACTORIES = setOf(
	"kotlin.collections.setOf", "kotlin.collections.mutableSetOf", "kotlin.collections.hashSetOf",
	"kotlin.collections.emptySet",
)
internal val MAP_FACTORIES = setOf(
	"kotlin.collections.mapOf", "kotlin.collections.mutableMapOf", "kotlin.collections.hashMapOf",
	"kotlin.collections.emptyMap",
)
internal val COLLECTION_OPS = setOf(
	"map", "filter", "take", "drop", "reversed", "distinct", "toList",
	"count", "any", "none", "all", "first", "last", "contains", "fold", "joinToString", "forEach",
	"firstOrNull", "lastOrNull", "isEmpty", "isNotEmpty", "sum", "sumOf", "sorted", "maxOrNull", "minOrNull", "reduce",
	"maxByOrNull", "minByOrNull", "zip", "associateWith", "associateBy", "groupBy",
	"asSequence", "toSet", "takeWhile", "dropWhile", "single", "singleOrNull",
	"sortedDescending", "sortedBy", "sortedByDescending", "mapIndexed", "chunked", "filterNotNull",
	"mapNotNull", "flatMap", "flatten", "average", "indexOf",
	"partition", "withIndex", "associate", "scan", "runningFold", "windowed",
)

// Primitive array class -> its BCL element type, for lowering the sized constructor `IntArray(size){init}` to a real
// `new int[size]` + fill loop (a `kotlin.IntArray` object would otherwise be constructed — the wrong representation).
internal val ARRAY_CLASS_ELEM = mapOf(
	"kotlin.IntArray" to "kotlin.Int", "kotlin.LongArray" to "kotlin.Long", "kotlin.ShortArray" to "kotlin.Short", "kotlin.ByteArray" to "kotlin.Byte",
	"kotlin.CharArray" to "kotlin.Char", "kotlin.DoubleArray" to "kotlin.Double", "kotlin.FloatArray" to "kotlin.Float", "kotlin.BooleanArray" to "kotlin.Boolean",
)

// Int-range/-progression types whose for-loop can be counter-lowered (over get_first/get_last/get_step) when the source
// is a range VALUE (e.g. `for (i in indices)`), avoiding the iterator protocol + its covariant-return iterator.
internal val INT_PROGRESSION_FQ = setOf("kotlin.ranges.IntRange", "kotlin.ranges.IntProgression")

// Top-level reified enum intrinsics, lowered at the call site to the same BIR nodes as `T.values()`/`T.valueOf()`
// (all type args are reified on the CLR). See the interception block in BirEmitter's call lowering.
internal val ENUM_REIFIED_INTRINSICS = setOf(
	"kotlin.enumValues", "kotlin.enumValueOf", "kotlin.enums.enumEntries", "kotlin.enums.enumEntriesIntrinsic",
)

// Numeric conversions on a number receiver (`3.7.toInt()`) -> a CIL conv to this BIR type.
// Numeric conversion target as a Kotlin FQN (kotc emits the FQN; ilemit selects the CIL conv opcode). The
// `conv` node stays a primitive-IL op; only its `to` type becomes a structured Kotlin-FQN identity (no `int`).
internal val NUMBER_CONV = mapOf(
	"toInt" to "kotlin.Int", "toLong" to "kotlin.Long", "toDouble" to "kotlin.Double", "toFloat" to "kotlin.Float",
	"toShort" to "kotlin.Short", "toByte" to "kotlin.Byte", "toChar" to "kotlin.Char",
)
internal val NUMERIC_FQ = setOf(
	"kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte",
	"kotlin.Double", "kotlin.Float", "kotlin.Char",
)
// Value-type primitives -> BIR element type (for Nullable<T> representation of `T?`).
internal val PRIMITIVE_EQ_FQ = setOf(
	"kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte",
	"kotlin.Double", "kotlin.Float", "kotlin.Boolean", "kotlin.Char",
)

// Kotlin types whose values ARE CLR primitives (the signed/bool/char primitives + the unsigned inline classes,
// which lower to native CLR unsigned primitives; unsigned arithmetic is already frontend-lowered to plain ops).
// ONLY these may have their operators lowered to raw CIL bin/un ops — any other kotlin.* owner (a VALUE CLASS
// like kotlin.time.Duration) keeps its member operator as a real method call.
internal val PRIMITIVE_OP_FQ = PRIMITIVE_EQ_FQ + setOf("kotlin.UInt", "kotlin.ULong", "kotlin.UByte", "kotlin.UShort")

// No kotlin.* -> System.* exception map here: that CLR knowledge belongs in bir2cir. The
// stdlib's exception classes carry `@kotlin.clr.ClrTypeAlias("System.X")`, and bir2cir reads that off the ref.dll
// to lower throw/catch/supertype/construction (the same @ClrTypeAlias path that lowers the collections). kotc emits
// the bare `kotlin.*Exception` FQN and nothing more. See MEMORY `exception-map-to-clrtypealias`.
