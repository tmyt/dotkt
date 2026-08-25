import kotlin.clr.ClrEnum

interface ClrEnumRichMarker

@ClrEnum
enum class ClrEnumRichShape(value: Int) : ClrEnumRichMarker {
    ENTRY(1) {
        fun entryMethod(): Int = 1
    };

    init {
        1.hashCode()
    }

    fun instanceMethod(): Int = 2
}
