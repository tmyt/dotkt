package DotKt.Runtime.CompilerServices

// Ephemeron keys do not keep collectible runtime types/assemblies alive. Cells
// cache only immutable type facts, never a collection instance or its history.
@kotlin.clr.ClrTypeAlias("System.Runtime.CompilerServices.ConditionalWeakTable")
private class EqualityTypeCache<K : Any, V : Any> {
    constructor()
    @kotlin.clr.ClrIntrinsic("GetOrCreateValue")
    fun get(key: K): V = error("CLR binding")
}

internal class EqualityTypePlan(val family: Int, val storage: StarProjectionType?, val nativeHashOverride: Boolean)
internal class EqualityEntryAccess(val key: StarProjectionMethod, val value: StarProjectionMethod)

private class EqualityCacheCell public constructor() {
    var plan: EqualityTypePlan? = null
    var entry: EqualityEntryAccess? = null
}

private val equalityTypeCache = EqualityTypeCache<StarProjectionType, EqualityCacheCell>()

@kotlin.clr.ClrIntrinsic("System.Threading.Monitor.Enter")
private fun enterEqualityCell(cell: Any): Unit = error("CLR binding")
@kotlin.clr.ClrIntrinsic("System.Threading.Monitor.Exit")
private fun exitEqualityCell(cell: Any): Unit = error("CLR binding")

internal fun equalityTypePlan(type: StarProjectionType): EqualityTypePlan {
    val cell = equalityTypeCache.get(type)
    enterEqualityCell(cell)
    try {
        val cached = cell.plan
        if (cached !== null) return cached
        val created = classifyEqualityType(type)
        cell.plan = created
        return created
    } finally { exitEqualityCell(cell) }
}

internal fun equalityEntryAccess(type: StarProjectionType): EqualityEntryAccess {
    val cell = equalityTypeCache.get(type)
    enterEqualityCell(cell)
    try {
        val cached = cell.entry
        if (cached !== null) return cached
        val created = EqualityEntryAccess(erasedProjectedMethod(type, "get_Key", 0),
            erasedProjectedMethod(type, "get_Value", 0))
        cell.entry = created
        return created
    } finally { exitEqualityCell(cell) }
}
