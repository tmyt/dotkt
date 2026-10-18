private fun <T : Enum<T>> ordinal(value: T): Int = value.ordinal
private fun <T : Enum<T>> compare(left: T, right: T): Int = left.compareTo(right)
inline fun <reified T : Enum<T>> valuesOf(): Array<T> = enumValues<T>()
enum class RichControl(val marker: Int) {
    First(1) { override fun read(): Int = marker }, Second(2);
    open fun read(): Int = marker
}

fun main() {
    val first = MetadataOrder.Api.First()
    check(first.ordinal == 0 && ordinal(first) == 0)
    check(MetadataOrder.Api.Bits(MetadataOrder.Code.First) == 103)
    check(enumValues<MetadataOrder.Code>().joinToString(":") { MetadataOrder.Api.Bits(it).toString() } == "103:-7")
    val factory: () -> Array<MetadataOrder.Code> = ::valuesOf
    check(factory().joinToString(":") { MetadataOrder.Api.Bits(it).toString() } == "103:-7")
    check(MetadataOrder.Api.Hidden().ordinal == -1 && ordinal(MetadataOrder.Api.Hidden()) == -1)
    check(MetadataOrder.Api.Unknown().ordinal == -1 && ordinal(MetadataOrder.Api.Unknown()) == -1)
    val unbound = MetadataOrder.Code::ordinal
    val bound = first::ordinal
    check(unbound.get(first) == 0 && bound.get() == 0)
    val compare: (MetadataOrder.Code, MetadataOrder.Code) -> Int = MetadataOrder.Code::compareTo
    check(compare(MetadataOrder.Code.First, MetadataOrder.Code.Second) == -1)
    val boundCompare: (MetadataOrder.Code) -> Int = first::compareTo
    check(boundCompare(MetadataOrder.Code.Second) == -1)

    val character = NativeChar.Api.First()
    check(NativeChar.Api.Bits(character) == 122)
    check(NativeChar.Api.Bits(NativeChar.Code.Second) == 97)
    check(character.ordinal == 0 && ordinal(character) == 0)
    check(enumValues<NativeChar.Code>().joinToString(":") { NativeChar.Api.Bits(it).toString() } == "122:97:122")
    val characters: () -> Array<NativeChar.Code> = ::valuesOf
    check(characters().joinToString(":") { NativeChar.Api.Bits(it).toString() } == "122:97:122")
    check(RichControl.First.compareTo(RichControl.Second) == -1)
    check(compare(RichControl.First, RichControl.Second) == -1)
    check(compare(RichControl.Second, RichControl.First) == 1)
    println("native enum metadata order: OK")
}
