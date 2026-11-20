package DotKt.Runtime.CompilerServices

/** Implemented by bir2cir for nominal classes with a Kotlin function supertype. */
@PublishedApi
internal interface KotlinFunctionSource {
    fun dotktCreateFunctionView(arity: Int): FunctionDelegate?
}

@kotlin.clr.ClrTypeAlias("System.Runtime.CompilerServices.ConditionalWeakTable")
private class FunctionViewTable<K : Any, V : Any> {
    constructor()
    @kotlin.clr.ClrIntrinsic("GetOrCreateValue") fun cell(key: K): V = error("CLR binding")
}

private class FunctionViews public constructor() {
    var byArity: MutableMap<Int, Any>? = null
}
private val functionViews = FunctionViewTable<KotlinFunctionSource, FunctionViews>()

@kotlin.clr.ClrIntrinsic("System.Threading.Monitor.Enter")
private fun enterFunctionViews(cell: Any): Unit = error("CLR binding")
@kotlin.clr.ClrIntrinsic("System.Threading.Monitor.Exit")
private fun exitFunctionViews(cell: Any): Unit = error("CLR binding")

@PublishedApi
internal fun kotlinFunctionView(value: Any?, arity: Int): Any? {
    if (value !is KotlinFunctionSource) return value
    val cell = functionViews.cell(value)
    enterFunctionViews(cell)
    try {
        val views = cell.byArity ?: mutableMapOf<Int, Any>().also { cell.byArity = it }
        val cached = views[arity]
        if (cached != null) return cached
        val view = value.dotktCreateFunctionView(arity) ?: return value
        views[arity] = view
        return view
    } finally { exitFunctionViews(cell) }
}

// A generic value slot can close over the original nominal class or over
// its callable delegate. Keep the original unless the exact slot admits a view.
@PublishedApi
internal fun kotlinFunctionSlot(value: Any?, slot: StarProjectionType): Any? {
    if (value !is KotlinFunctionSource || slot.isAssignableFrom(value.starProjectionRuntimeType())) return value
    if (!(FunctionDelegate::class as StarProjectionType).isAssignableFrom(slot)) return value
    val invoke = slot.functionInvoke("Invoke") ?: return value
    val view = kotlinFunctionView(value, invoke.getParameters().size) ?: return value
    return if (slot.isAssignableFrom(view.starProjectionRuntimeType())) view else value
}

@kotlin.clr.ClrTypeAlias("System.Delegate")
@PublishedApi
internal abstract class FunctionDelegate {
    @kotlin.clr.ClrIntrinsic("get_Target") abstract fun boundTarget(): Any?
}

// Only compiler-published views represent their receiver. A user-created bound
// callable reference, even to the very same receiver, remains a distinct value.
@PublishedApi
internal fun kotlinFunctionObject(value: Any?): Any? {
    val target = (value as? FunctionDelegate)?.boundTarget() as? KotlinFunctionSource ?: return value
    val cell = functionViews.cell(target)
    enterFunctionViews(cell)
    try {
        val views = cell.byArity ?: return value
        for (view in views.values) if (view === value) return target
        return value
    } finally { exitFunctionViews(cell) }
}
