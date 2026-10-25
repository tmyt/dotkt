package roundtrip.nullablemap

fun makeNullableMap(): HashMap<String?, String?> = hashMapOf(null to "null", "key" to null)
fun makeOrderedNullableMap(): LinkedHashMap<String?, String?> = linkedMapOf(null to "null", "key" to null)
fun copyNativeMap(source: Map<String?, String?>) = source.toClrDictionary()
