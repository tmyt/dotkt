package DotKt.Runtime.CompilerServices

// Both keys are ephemerons. A cached type/view decision retains neither collection instances
// nor collectible assemblies, and has no dependence on the object's originating Kotlin view.
@kotlin.clr.ClrTypeAlias("System.Runtime.CompilerServices.ConditionalWeakTable")
private class ExactMutableWeakCache<K : Any, V : Any> {
    constructor()
    @kotlin.clr.ClrIntrinsic("GetOrCreateValue")
    fun get(key: K): V = error("CLR binding")
}

private class ExactMutableTypeCell public constructor() {
    val views = ExactMutableWeakCache<StarProjectionType, ExactMutableChoiceCell>()
    @kotlin.concurrent.Volatile
    var classified: Boolean = false
    var stableInterfaces: Boolean = false
}

@kotlin.clr.ClrTypeAlias("System.Runtime.InteropServices.IDynamicInterfaceCastable")
private interface ExactMutableDynamicInterfaces

private class ExactMutableChoiceCell public constructor() {
    @kotlin.concurrent.Volatile
    var known: Int = 0
    var allowed: Int = 0
}

private val exactMutableViews = ExactMutableWeakCache<StarProjectionType, ExactMutableTypeCell>()

@kotlin.clr.ClrIntrinsic("System.Threading.Monitor.Enter")
private fun enterExactMutableCell(cell: Any): Unit = error("CLR binding")
@kotlin.clr.ClrIntrinsic("System.Threading.Monitor.Exit")
private fun exitExactMutableCell(cell: Any): Unit = error("CLR binding")

// Called before bir2cir's invariant native-interface test. That test alone is insufficient:
// dictionary entry storage can be ineligible, and CLR arrays have special interface covariance.
// Reuse the real operation selector, without throwing before the operation's arguments are evaluated.
@PublishedApi
internal fun canUseExactMutableView(receiver: Any, sourceView: Any?, family: Int): Boolean {
    if (sourceView == null) return false
    val preferred = sourceView as StarProjectionType
    val runtimeType = receiver.starProjectionRuntimeType()
    val typeCell = exactMutableViews.get(runtimeType)
    if (!typeCell.classified) {
        enterExactMutableCell(typeCell)
        try {
            if (!typeCell.classified) {
                // Custom/COM interface casts can execute user code or depend on the instance.
                // Keep those operations on the original path rather than cache an instance-varying decision.
                typeCell.stableInterfaces = !runtimeType.isComObject && receiver !is ExactMutableDynamicInterfaces
                typeCell.classified = true
            }
        } finally { exitExactMutableCell(typeCell) }
    }
    if (!typeCell.stableInterfaces) return false
    val cell = typeCell.views.get(preferred)
    val bit = 1 shl family
    // Publish allowed before the release store to known; a hit's acquire read needs no monitor.
    if ((cell.known and bit) != 0) return (cell.allowed and bit) != 0
    enterExactMutableCell(cell)
    try {
        if ((cell.known and bit) != 0) return (cell.allowed and bit) != 0
        val element = preferred.getGenericArguments()[0]
        val nativeFamily = if (family == 4 && receiver is KotlinMutableSetClassifier) 5 else family
        val definition = when (nativeFamily) {
            4 -> "System.Collections.Generic.ISet`1"
            5 -> "System.Collections.Generic.ICollection`1"
            6 -> "System.Collections.Generic.IList`1"
            else -> return false
        }
        val selected = findErasedProjectedView(receiver, definition, definition,
            excludeDictionaryStorage = nativeFamily != 4, elementWitness = element, failOnAmbiguity = false)
        val allowed = selected != null && selected.getGenericArguments()[0] == element
        if (allowed) cell.allowed = cell.allowed or bit
        cell.known = cell.known or bit
        return allowed
    } finally { exitExactMutableCell(cell) }
}
