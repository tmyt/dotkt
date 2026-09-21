package roundtrip.setarguments

fun importedSetSize(value: Set<Any?>): Int = value.size
fun importedSetView(value: Set<Any?>): Set<Any?> = value
fun forwardImportedSet(value: Set<*>): Set<Any?> = importedSetView(value)
fun importedNullableSetSize(value: Set<Any?>?): Int = value?.size ?: -1
fun forwardImportedNullableSet(value: Set<*>?): Int = importedNullableSetSize(value)
