package DotKt.Runtime.CompilerServices

// Kotlin Map operations are not the IDictionary contract: notably get is null-on-missing,
// and keys/values/entries may have authored overrides. Keep these semantic slots separate
// from the native CLR dictionary faces. Erased arguments preserve dispatch across K/V views.
@PublishedApi
internal interface KotlinMapSlots {
    fun dotktMapSize(): Int
    fun dotktMapIsEmpty(): Boolean
    fun dotktMapContainsKey(key: Any?): Boolean
    fun dotktMapContainsValue(value: Any?): Boolean
    fun dotktMapGet(key: Any?): Any?
    fun dotktMapEntries(): Any
    fun dotktMapKeys(): Any
    fun dotktMapValues(): Any
}

@PublishedApi
internal interface KotlinMutableMapSlots {
    fun dotktMapPut(key: Any?, value: Any?): Any?
    fun dotktMapRemove(key: Any?): Any?
    fun dotktMapPutAll(from: Any)
    fun dotktMapClear()
}

// Defaulted operations are independent capabilities: overriding one must not require
// a class to implement unrelated defaults or replace their ordinary fallback bodies.
@PublishedApi
internal interface KotlinMapGetOrDefaultSlot {
    fun dotktMapGetOrDefault(key: Any?, defaultValue: Any?): Any?
}

@PublishedApi
internal interface KotlinMapRemoveEntrySlot {
    fun dotktMapRemoveEntry(key: Any?, value: Any?): Boolean
}
