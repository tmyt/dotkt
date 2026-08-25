import kotlin.clr.ClrEnum

fun clrEnumComputedValue(): Int = 1

@ClrEnum
enum class ClrEnumNonConstantEntry(value: Int) {
    ONE(clrEnumComputedValue()),
}
