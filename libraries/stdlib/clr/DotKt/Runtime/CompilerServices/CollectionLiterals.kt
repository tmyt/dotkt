package DotKt.Runtime.CompilerServices

// An erased Kotlin collection declaration still needs a distinct System.Type for reflection
// and for CLR attribute arguments. These tokens describe declarations, never value storage.
@PublishedApi internal sealed class CollectionLiteralDescriptor(val sourceName: String, val isInterface: Boolean = true) {
    abstract fun accepts(value: Any?): Boolean
}

@PublishedApi internal object IterableLiteral : CollectionLiteralDescriptor("kotlin.collections.Iterable") {
    override fun accepts(value: Any?) = value is Iterable<*>
}
@PublishedApi internal object MutableIterableLiteral : CollectionLiteralDescriptor("kotlin.collections.MutableIterable") {
    override fun accepts(value: Any?) = value is MutableIterable<*>
}
@PublishedApi internal object CollectionLiteral : CollectionLiteralDescriptor("kotlin.collections.Collection") {
    override fun accepts(value: Any?) = value is Collection<*>
}
@PublishedApi internal object MutableCollectionLiteral : CollectionLiteralDescriptor("kotlin.collections.MutableCollection") {
    override fun accepts(value: Any?) = value is MutableCollection<*>
}
@PublishedApi internal object ListLiteral : CollectionLiteralDescriptor("kotlin.collections.List") {
    override fun accepts(value: Any?) = value is List<*>
}
@PublishedApi internal object MutableListLiteral : CollectionLiteralDescriptor("kotlin.collections.MutableList") {
    override fun accepts(value: Any?) = value is MutableList<*>
}
@PublishedApi internal object SetLiteral : CollectionLiteralDescriptor("kotlin.collections.Set") {
    override fun accepts(value: Any?) = value is Set<*>
}
@PublishedApi internal object MutableSetLiteral : CollectionLiteralDescriptor("kotlin.collections.MutableSet") {
    override fun accepts(value: Any?) = value is MutableSet<*>
}
@PublishedApi internal object MapLiteral : CollectionLiteralDescriptor("kotlin.collections.Map") {
    override fun accepts(value: Any?) = value is Map<*, *>
}
@PublishedApi internal object MutableMapLiteral : CollectionLiteralDescriptor("kotlin.collections.MutableMap") {
    override fun accepts(value: Any?) = value is MutableMap<*, *>
}
@PublishedApi internal object ArrayListLiteral : CollectionLiteralDescriptor("kotlin.collections.ArrayList", false) {
    override fun accepts(value: Any?) = value is ArrayList<*>
}
@PublishedApi internal object HashSetLiteral : CollectionLiteralDescriptor("kotlin.collections.HashSet", false) {
    override fun accepts(value: Any?) = value is HashSet<*>
}

internal fun collectionLiteral(type: StarProjectionType): CollectionLiteralDescriptor? {
    if (!(CollectionLiteralDescriptor::class as StarProjectionType).isAssignableFrom(type)) return null
    return when (type) {
        IterableLiteral::class as StarProjectionType -> IterableLiteral
        MutableIterableLiteral::class as StarProjectionType -> MutableIterableLiteral
        CollectionLiteral::class as StarProjectionType -> CollectionLiteral
        MutableCollectionLiteral::class as StarProjectionType -> MutableCollectionLiteral
        ListLiteral::class as StarProjectionType -> ListLiteral
        MutableListLiteral::class as StarProjectionType -> MutableListLiteral
        SetLiteral::class as StarProjectionType -> SetLiteral
        MutableSetLiteral::class as StarProjectionType -> MutableSetLiteral
        MapLiteral::class as StarProjectionType -> MapLiteral
        MutableMapLiteral::class as StarProjectionType -> MutableMapLiteral
        ArrayListLiteral::class as StarProjectionType -> ArrayListLiteral
        HashSetLiteral::class as StarProjectionType -> HashSetLiteral
        else -> null
    }
}
