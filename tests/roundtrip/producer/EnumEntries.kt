package roundtrip.entries

import kotlin.enums.EnumEntries
import kotlin.enums.enumEntries

enum class BasicEntry { FIRST, SECOND }
enum class RichEntry(val score: Int) { FIRST(7), SECOND(9) }

enum class SuperRenderedEntry(val prefix: String) {
    FIRST("first"),
    SECOND("second") { override fun toString(): String = "entry:" + super.toString() };

    override fun toString(): String = prefix + ":" + super.toString()
}

fun basicEntries(): EnumEntries<BasicEntry> = BasicEntry.entries
fun richEntries(): EnumEntries<RichEntry> = enumEntries<RichEntry>()
fun richName(value: RichEntry): String = value.name
fun richOrdinal(value: RichEntry): Int = value.ordinal
fun richCompare(left: RichEntry, right: RichEntry): Int = left.compareTo(right)
inline fun <reified E : Enum<E>> sharedEntries(): EnumEntries<E> = enumEntries<E>()
fun <E : Enum<E>> relayEntries(entries: EnumEntries<E>): EnumEntries<E> = entries
