package roundtrip.entriesidentity

import kotlin.enums.EnumEntries

enum class IdentityEntry { FIRST, SECOND }
private var entryTrace = ""
fun resetEntryTrace() { entryTrace = "" }
fun readEntryTrace(): String = entryTrace
fun entries(): EnumEntries<IdentityEntry> {
    entryTrace += "f"
    return IdentityEntry.entries
}
val entries: EnumEntries<IdentityEntry>
    get() {
        entryTrace += "p"
        return IdentityEntry.entries
    }
fun entries(index: Int): List<IdentityEntry> {
    entryTrace += "o"
    return listOf(IdentityEntry.entries[index])
}
fun entryArgument(): Int {
    entryTrace += "a"
    return 1
}
enum class CompanionIdentityEntry(val score: Int) {
    FIRST(2), SECOND(3);
    companion object {
        fun entries(): EnumEntries<CompanionIdentityEntry> {
            entryTrace += "c"
            return CompanionIdentityEntry.entries
        }
    }
}
class EntryIdentitySource {
    fun entries(): EnumEntries<IdentityEntry> {
        entryTrace += "m"
        return IdentityEntry.entries
    }
}
fun entrySource(): EntryIdentitySource {
    entryTrace += "r"
    return EntryIdentitySource()
}
