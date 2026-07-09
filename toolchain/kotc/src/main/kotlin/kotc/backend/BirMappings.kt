package kotc.backend

// The last Kotlin->.NET mapping still living in kotc: the unsigned specialized arrays. Every other table that was
// once here is GONE from kotc — array/primitive-ness is now read from the IR type system (isPrimitiveArray /
// isPrimitiveType), and the CLR binding of every stdlib symbol (operators, math/text/char ops, factories, conv,
// exceptions, ranges, enum intrinsics) is bir2cir's: kotc emits the faithful call/type by identity and bir2cir
// recognizes it in the CIR + binds it off the ref.dll @Clr* metadata.
//
// WHY unsigned STILL needs a kotc map (the one exception): a signed `IntArray` is a Kotlin builtin with no source
// body, so it is uniformly native `int[]` and needs no table. A `UByteArray` is instead a stdlib VALUE CLASS
// (`UByteArray(private val storage: ByteArray)`), so its representation is BUILD-MODE-dependent — the stdlib's OWN
// build must emit the value class (so `UByteArray.kt` / `_UArrays.kt` compile against a real `storage` field),
// while a consumer build lowers it to native `Byte[]`. This element map drives that consumer-only (`!stdlibCompile`)
// native lowering: element = the unsigned scalar (bir2cir lowers kotlin.UByte -> `ubyte`, ilemit -> System.Byte[]).
// It dissolves once unsigned is unified to native like signed (#76: @ClrTypeAlias UByteArray->native + a
// storage-free stdlib source), after which this file is deleted.
internal val UNSIGNED_ARRAY_ELEM = mapOf(
	"kotlin.UByteArray" to "kotlin.UByte", "kotlin.UShortArray" to "kotlin.UShort",
	"kotlin.UIntArray" to "kotlin.UInt", "kotlin.ULongArray" to "kotlin.ULong",
)
