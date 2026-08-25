import kotlin.clr.ClrEnum

@ClrEnum
enum class ClrEnumSecondaryConstructor(value: Int) {
    ENTRY(1);

    constructor(value: Short) : this(value.toInt())
}
