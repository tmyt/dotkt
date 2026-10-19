package roundtrip.entries

import kotlin.enums.EnumEntries
import kotlin.enums.enumEntries

enum class BasicEntry { FIRST, SECOND }
enum class RichEntry(val score: Int) { FIRST(7), SECOND(9) }

fun basicEntries(): EnumEntries<BasicEntry> = BasicEntry.entries
fun richEntries(): EnumEntries<RichEntry> = enumEntries<RichEntry>()
inline fun <reified E : Enum<E>> sharedEntries(): EnumEntries<E> = enumEntries<E>()
fun <E : Enum<E>> relayEntries(entries: EnumEntries<E>): EnumEntries<E> = entries
