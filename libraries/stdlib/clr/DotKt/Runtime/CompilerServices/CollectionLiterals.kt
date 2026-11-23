package DotKt.Runtime.CompilerServices

// An erased Kotlin collection declaration still needs a distinct System.Type for reflection
// and for CLR attribute arguments. These tokens describe declarations, never value storage.
internal sealed class CollectionLiteralDescriptor(val sourceName: String) {
    abstract fun accepts(value: Any?): Boolean
}

internal object IterableLiteral : CollectionLiteralDescriptor("kotlin.collections.Iterable") {
    override fun accepts(value: Any?) = value is Iterable<*>
}
internal object MutableIterableLiteral : CollectionLiteralDescriptor("kotlin.collections.MutableIterable") {
    override fun accepts(value: Any?) = value is MutableIterable<*>
}
internal object CollectionLiteral : CollectionLiteralDescriptor("kotlin.collections.Collection") {
    override fun accepts(value: Any?) = value is Collection<*>
}
internal object MutableCollectionLiteral : CollectionLiteralDescriptor("kotlin.collections.MutableCollection") {
    override fun accepts(value: Any?) = value is MutableCollection<*>
}
internal object ListLiteral : CollectionLiteralDescriptor("kotlin.collections.List") {
    override fun accepts(value: Any?) = value is List<*>
}
internal object MutableListLiteral : CollectionLiteralDescriptor("kotlin.collections.MutableList") {
    override fun accepts(value: Any?) = value is MutableList<*>
}
internal object SetLiteral : CollectionLiteralDescriptor("kotlin.collections.Set") {
    override fun accepts(value: Any?) = value is Set<*>
}
internal object MutableSetLiteral : CollectionLiteralDescriptor("kotlin.collections.MutableSet") {
    override fun accepts(value: Any?) = value is MutableSet<*>
}
internal object MapLiteral : CollectionLiteralDescriptor("kotlin.collections.Map") {
    override fun accepts(value: Any?) = value is Map<*, *>
}
internal object MutableMapLiteral : CollectionLiteralDescriptor("kotlin.collections.MutableMap") {
    override fun accepts(value: Any?) = value is MutableMap<*, *>
}
internal object ArrayListLiteral : CollectionLiteralDescriptor("kotlin.collections.ArrayList") {
    override fun accepts(value: Any?) = value is ArrayList<*>
}
internal object HashSetLiteral : CollectionLiteralDescriptor("kotlin.collections.HashSet") {
    override fun accepts(value: Any?) = value is HashSet<*>
}

// Unlike the collection interfaces, these concrete aliases have a unique BCL implementation.
// Compare TypeDef tokens, not assignability: a subclass retains its own runtime class.
@kotlin.clr.ClrTypeAlias("System.Collections.Generic.List`1")
private interface NativeArrayListDefinition
@kotlin.clr.ClrTypeAlias("System.Collections.Generic.HashSet`1")
private interface NativeHashSetDefinition

internal fun nativeCollectionLiteral(type: StarProjectionType): StarProjectionType? {
    if (!type.isGenericType || type.isGenericTypeDefinition) return null
    return when (type.getGenericTypeDefinition()) {
        NativeArrayListDefinition::class as StarProjectionType -> ArrayListLiteral::class as StarProjectionType
        NativeHashSetDefinition::class as StarProjectionType -> HashSetLiteral::class as StarProjectionType
        else -> null
    }
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
