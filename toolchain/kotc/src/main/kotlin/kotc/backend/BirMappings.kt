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

internal val PRIMITIVE_ARRAY_ELEM = mapOf(
	"kotlin.IntArray" to "int", "kotlin.LongArray" to "long", "kotlin.DoubleArray" to "double",
	"kotlin.FloatArray" to "float", "kotlin.BooleanArray" to "bool", "kotlin.CharArray" to "char",
	"kotlin.ByteArray" to "byte", "kotlin.ShortArray" to "short",
)
internal val ARRAY_FACTORY_NAMES = setOf(
	"arrayOf", "intArrayOf", "longArrayOf", "doubleArrayOf",
	"floatArrayOf", "booleanArrayOf", "charArrayOf", "byteArrayOf", "shortArrayOf",
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
// MutableList/MutableCollection INSTANCE mutation members (not COLLECTION_OPS extension ops) -> the BCL List<T>
// method. Kotlin's collections lower to System.Collections.Generic.List<T>, so `list.add(x)` etc. bind to its methods.
internal val COLLECTION_MEMBER = mapOf(
	"add" to "Add", "remove" to "Remove", "clear" to "Clear", "removeAt" to "RemoveAt",
	"contains" to "Contains", "indexOf" to "IndexOf",
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
	"kotlin.IntArray" to "int", "kotlin.LongArray" to "long", "kotlin.ShortArray" to "short", "kotlin.ByteArray" to "byte",
	"kotlin.CharArray" to "char", "kotlin.DoubleArray" to "double", "kotlin.FloatArray" to "float", "kotlin.BooleanArray" to "bool",
)

// java.util.SequencedCollection (JDK21) leaks its members onto kotlin.collections.List/MutableList when the frontend
// reads the JVM builtins. On the CLR these are pure JVM-isms (IReadOnlyList/IList have no getFirst/addFirst/…), so an
// ABSTRACT injected interface slot has no implementer -> "method does not have an implementation". Drop them (discard
// the JVM-ism); a concrete type's REAL addFirst/removeFirst (with a body, e.g. ArrayDeque) is emitted independently.
// `reversed` is included: the SequencedCollection member leaks as ABSTRACT; the real `kotlin.collections.reversed`
// EXTENSION (a top-level function with a body) handles `list.reversed()` calls, so dropping the member slot is safe.
internal val SEQUENCED_COLLECTION_LEAK = setOf("getFirst", "getLast", "addFirst", "addLast", "removeFirst", "removeLast", "reversed")

// Int-range/-progression types whose for-loop can be counter-lowered (over get_first/get_last/get_step) when the source
// is a range VALUE (e.g. `for (i in indices)`), avoiding the iterator protocol + its covariant-return iterator.
internal val INT_PROGRESSION_FQ = setOf("kotlin.ranges.IntRange", "kotlin.ranges.IntProgression")

// Top-level reified enum intrinsics, lowered at the call site to the same BIR nodes as `T.values()`/`T.valueOf()`
// (all type args are reified on the CLR). See the interception block in BirEmitter's call lowering.
internal val ENUM_REIFIED_INTRINSICS = setOf(
	"kotlin.enumValues", "kotlin.enumValueOf", "kotlin.enums.enumEntries", "kotlin.enums.enumEntriesIntrinsic",
)

// Numeric conversions on a number receiver (`3.7.toInt()`) -> a CIL conv to this BIR type.
internal val NUMBER_CONV = mapOf(
	"toInt" to "int", "toLong" to "long", "toDouble" to "double", "toFloat" to "float",
	"toShort" to "short", "toByte" to "byte", "toChar" to "char",
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

internal val VALUE_PRIM_BIR = mapOf(
	"kotlin.Int" to "int", "kotlin.Long" to "long", "kotlin.Short" to "short", "kotlin.Byte" to "byte",
	"kotlin.Double" to "double", "kotlin.Float" to "float", "kotlin.Boolean" to "bool", "kotlin.Char" to "char",
)

// The primitive-value BIR shorthands (VALUE_PRIM_BIR's range): a `birType` that already reads as one of these
// (e.g. a substituted generic `T -> int`) is a bare value primitive, so a `when`/`if` join with a `null` branch
// over it must be tagged `nullable:<shorthand>`.
internal val PRIMITIVE_SHORTHANDS = VALUE_PRIM_BIR.values.toSet()

// Kotlin types whose values ARE CLR primitives (the signed/bool/char primitives + the unsigned inline classes,
// which lower to native CLR unsigned primitives; unsigned arithmetic is already frontend-lowered to plain ops).
// ONLY these may have their operators lowered to raw CIL bin/un ops — any other kotlin.* owner (a VALUE CLASS
// like kotlin.time.Duration) keeps its member operator as a real method call.
internal val PRIMITIVE_OP_FQ = PRIMITIVE_EQ_FQ + setOf("kotlin.UInt", "kotlin.ULong", "kotlin.UByte", "kotlin.UShort")

// No kotlin.* -> System.* exception map here: that CLR knowledge belongs in bir2cir. The
// stdlib's exception classes carry `@kotlin.clr.ClrTypeAlias("System.X")`, and bir2cir reads that off the ref.dll
// to lower throw/catch/supertype/construction (the same @ClrTypeAlias path that lowers the collections). kotc emits
// the bare `kotlin.*Exception` FQN and nothing more. See MEMORY `exception-map-to-clrtypealias`.
