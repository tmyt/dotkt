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

// kotlin.math.* -> System.Math.* (ilemit picks the int/double overload by argTypes).
internal val MATH_FUNCS = mapOf(
	"abs" to "Abs", "max" to "Max", "min" to "Min", "sqrt" to "Sqrt", "pow" to "Pow",
	"round" to "Round", "floor" to "Floor", "ceil" to "Ceiling", "exp" to "Exp",
	"ln" to "Log", "log10" to "Log10", "sin" to "Sin", "cos" to "Cos", "tan" to "Tan",
	"asin" to "Asin", "acos" to "Acos", "atan" to "Atan", "atan2" to "Atan2",
	"sinh" to "Sinh", "cosh" to "Cosh", "tanh" to "Tanh",
	"asinh" to "Asinh", "acosh" to "Acosh", "atanh" to "Atanh",
	"log" to "Log", "log2" to "Log2", "truncate" to "Truncate", "cbrt" to "Cbrt", "withSign" to "CopySign",
)

// kotlin.text String ops -> .NET System.String instance methods.
internal val STRING_OPS = mapOf(
	"uppercase" to "ToUpper", "lowercase" to "ToLower", "trim" to "Trim",
	"trimStart" to "TrimStart", "trimEnd" to "TrimEnd", "substring" to "Substring",
	"replace" to "Replace", "startsWith" to "StartsWith", "endsWith" to "EndsWith",
	"contains" to "Contains", "indexOf" to "IndexOf", "padStart" to "PadLeft", "padEnd" to "PadRight",
)

// `"42".toInt()` etc. -> a static `Parse` on the target .NET numeric type.
internal val NUMBER_PARSE = mapOf(
	"toInt" to "System.Int32", "toLong" to "System.Int64", "toDouble" to "System.Double",
	"toFloat" to "System.Single", "toShort" to "System.Int16", "toByte" to "System.Byte",
)
// Char predicates / conversions -> static methods on System.Char.
internal val CHAR_OPS = mapOf(
	"isDigit" to "IsDigit", "isLetter" to "IsLetter", "isWhitespace" to "IsWhiteSpace",
	"isLetterOrDigit" to "IsLetterOrDigit", "uppercaseChar" to "ToUpper", "lowercaseChar" to "ToLower",
	"isUpperCase" to "IsUpper", "isLowerCase" to "IsLower",
)

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

// kotlin.* throwables -> their .NET counterpart. The frontend jar resolves these as our real `kotlin.*` exception
// classes (NOT java.* typealiases), so only the kotlin.* keys are live; the former java.lang.*/java.util.* keys were
// dead once no consumer fed kotc the JVM kotlin-stdlib.jar. `kotlin.ArithmeticException` maps to System.ArithmeticException
// so `catch (ArithmeticException)` catches the BCL System.DivideByZeroException that integer `a/0` throws.
internal val NET_EXCEPTIONS = mapOf(
	"kotlin.Throwable" to "System.Exception",
	"kotlin.Exception" to "System.Exception",
	"kotlin.RuntimeException" to "System.Exception",
	"kotlin.Error" to "System.Exception",
	"kotlin.ArithmeticException" to "System.ArithmeticException",
	"kotlin.IllegalArgumentException" to "System.ArgumentException",
	"kotlin.IllegalStateException" to "System.InvalidOperationException",
	"kotlin.IndexOutOfBoundsException" to "System.IndexOutOfRangeException",
	"kotlin.NullPointerException" to "System.NullReferenceException",
	"kotlin.UnsupportedOperationException" to "System.NotSupportedException",
	"kotlin.NoSuchElementException" to "System.InvalidOperationException",
)
internal val ATOMICFU_TYPES = setOf(
	"kotlinx.atomicfu.AtomicInt", "kotlinx.atomicfu.AtomicLong",
	"kotlinx.atomicfu.AtomicBoolean", "kotlinx.atomicfu.AtomicRef",
)
