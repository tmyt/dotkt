package roundtrip.portablesetiterator

fun linkedIterator(set: LinkedHashSet<String>): MutableIterator<String> = set.iterator()
fun nativeIterator(set: HashSet<Int?>): MutableIterator<Int?> = set.iterator()
