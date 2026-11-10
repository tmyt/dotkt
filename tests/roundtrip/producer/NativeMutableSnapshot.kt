package roundtrip.nativemutablesnapshot

fun <T> nativeSnapshotCursor(receiver: () -> MutableCollection<T>): MutableIterator<T> = receiver().iterator()

fun <T> erasedSnapshotNext(receiver: () -> MutableIterator<T>): Any? = receiver().next()
