package DotKt.Runtime.CompilerServices

import kotlin.clr.byref
import kotlin.clr.ClrRef

@kotlin.clr.ClrTypeAlias("System.Array")
@PublishedApi
internal interface ProjectedArrayVector {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Length") val length: Int
    @kotlin.clr.ClrIntrinsic("GetValue") fun read(index: Int): Any?
    @kotlin.clr.ClrIntrinsic("SetValue") fun write(value: Any?, index: Int)
}

@kotlin.clr.ClrTypeAlias("System.Runtime.CompilerServices.ConditionalWeakTable")
private class ProjectedArrayTable<K : Any, V : Any> {
    constructor()
    @kotlin.clr.ClrIntrinsic("Add") fun add(key: K, value: V): Unit = error("CLR binding")
    @kotlin.clr.ClrIntrinsic("GetOrCreateValue") fun cell(key: K): V = error("CLR binding")
    @kotlin.clr.ClrIntrinsic("TryGetValue") fun find(key: K, value: ClrRef<V?>): Boolean = error("CLR binding")
}

private class ProjectedArrayState(val element: StarProjectionType) {
    var overrides: MutableMap<Int, Any>? = null
    var exported = false
    var addressed: MutableSet<Int>? = null
}
private val projectedArrays = ProjectedArrayTable<ProjectedArrayVector, ProjectedArrayState>()

// A Kotlin function and a CLR callback can have the very same delegate type.
// Record the newly constructed Kotlin callable by identity, without changing
// what is stored in Kotlin, returned to C#, or used for delegate invocation.
private val kotlinCallables = ProjectedArrayTable<Any, Any>()
private val kotlinCallableTag = Any()

// The compiler constructs a fresh target and exactly one delegate together.
// Match the delegate, not just its target: another delegate (including a
// multicast delegate) can have the same last target.
@PublishedApi
internal open class KotlinClosureTarget {
    internal var publishedCallable: Any? = null
}

/** Implemented on compiler-authored CLR result views, not on the user's callable. */
@PublishedApi
internal interface KotlinCallbackEntry {
    fun dotktKotlinEntry(): Any
}

@kotlin.clr.ClrIntrinsic("System.Delegate.Combine")
private fun combineCallbackEntries(first: FunctionDelegate?, next: FunctionDelegate): FunctionDelegate = error("CLR binding")

// Only Kotlin Invoke can select this entry. A clone still has the same target;
// a multicast must preserve the complete ordered list, not just its last target.
@PublishedApi
internal fun kotlinCallbackEntry(value: Any?, slot: StarProjectionType): Any? {
    val delegate = value as? FunctionDelegate ?: return value
    if (delegate.hasSingleTarget()) {
        val raw = (delegate.boundTarget() as? KotlinCallbackEntry)?.dotktKotlinEntry() ?: return value
        return if (slot.isAssignableFrom(raw.starProjectionRuntimeType())) raw else value
    }
    val members = delegate.invocationList()
    var changed = false
    val type = delegate.starProjectionRuntimeType()
    for (i in members.indices) {
        val raw = (members[i].boundTarget() as? KotlinCallbackEntry)?.dotktKotlinEntry() as? FunctionDelegate ?: continue
        if (raw.starProjectionRuntimeType() == type) { members[i] = raw; changed = true }
    }
    if (!changed) return value
    var result: FunctionDelegate? = null
    for (member in members) result = combineCallbackEntries(result, member)
    return result
}

@PublishedApi
internal fun recordKotlinClosure(value: Any): Any {
    val target = (value as? FunctionDelegate)?.boundTarget() as? KotlinClosureTarget
        ?: return recordKotlinCallable(value)
    target.publishedCallable = value
    return value
}

@PublishedApi
internal fun recordKotlinCallable(value: Any): Any {
    kotlinCallables.add(value, kotlinCallableTag)
    return value
}

@PublishedApi
internal fun callablePublishesArrays(value: Any?): Boolean {
    if (value == null) return false
    val delegate = value as? FunctionDelegate ?: return true
    if (delegate.hasSingleTarget()) return !isKotlinCallable(delegate)
    // Combine creates a new delegate identity. Publication is a property of
    // every target that will run, not just the last target exposed by Target.
    for (member in delegate.invocationList()) if (!isKotlinCallable(member)) return true
    return false
}

private fun isKotlinCallable(value: FunctionDelegate): Boolean {
    val target = value.boundTarget() as? KotlinClosureTarget
    if (target?.publishedCallable === value) return true
    var tag: Any? = null
    return kotlinCallables.find(value, byref(tag))
}

private class ResultExports public constructor() {
    var byType: MutableMap<StarProjectionType, Any>? = null
}
private val resultExports = ProjectedArrayTable<Any, ResultExports>()

// For arguments every multicast target matters; only the last supplies the result.
@PublishedApi
internal fun existingNativeResultView(original: Any, type: StarProjectionType): Any? {
    val value = original as FunctionDelegate
    val last = if (value.hasSingleTarget()) value else {
        val targets = value.invocationList()
        targets[targets.size - 1]
    }
    if (!isKotlinCallable(last)) return original
    return cachedNativeDelegateView(original, type)
}

@PublishedApi
internal fun cachedNativeDelegateView(original: Any, type: StarProjectionType): Any? {
    var existing: ResultExports? = null
    resultExports.find(original, byref(existing))
    val cell = existing ?: return null
    enterArrayState(cell)
    try { return cell.byType?.get(type) } finally { exitArrayState(cell) }
}

@PublishedApi
internal fun publishNativeResultView(original: Any, candidate: Any): Any {
    val cell = resultExports.cell(original)
    val type = candidate.starProjectionRuntimeType()
    enterArrayState(cell)
    try {
        val views = cell.byType ?: mutableMapOf<StarProjectionType, Any>().also { cell.byType = it }
        return views[type] ?: candidate.also { views[type] = it }
    } finally { exitArrayState(cell) }
}

@kotlin.clr.ClrTypeAlias("System.ArrayTypeMismatchException")
private class ArrayComponentMismatch : Throwable()

@kotlin.clr.ClrIntrinsic("IsInstanceOfType")
private fun StarProjectionType.acceptsArrayValue(value: Any): Boolean = error("CLR binding")

@kotlin.clr.ClrIntrinsic("System.Threading.Monitor.Enter")
private fun enterArrayState(state: Any): Unit = error("CLR binding")
@kotlin.clr.ClrIntrinsic("System.Threading.Monitor.Exit")
private fun exitArrayState(state: Any): Unit = error("CLR binding")

private fun stateOf(array: ProjectedArrayVector): ProjectedArrayState? {
    var state: ProjectedArrayState? = null
    projectedArrays.find(array, byref(state))
    return state
}

// Kotlin's unchecked substitution can erase a generic argument, never the
// enclosing classifier. In a jagged vector each array dimension is retained.
private fun logicalComponentAccepts(expected: StarProjectionType, actual: StarProjectionType): Boolean {
    if (expected.isAssignableFrom(actual)) return true
    if (expected.isSzArray && actual.isSzArray) {
        val to = expected.getElementType()!!
        val from = actual.getElementType()!!
        return !to.isValueType && !from.isValueType && logicalComponentAccepts(to, from)
    }
    val classifier = runtimeKClass(expected)
    if (classifier == expected) return false
    return if (classifier.isGenericTypeDefinition) starProjectionHasView(actual, classifier)
        else classifier.isAssignableFrom(actual)
}

// Register exactly once, at allocation of a Kotlin array with a narrower
// physical reference component. Foreign and ordinary CLR arrays are not opted in.
@PublishedApi
internal fun registerProjectedArray(array: ProjectedArrayVector): ProjectedArrayVector {
    projectedArrays.add(array, ProjectedArrayState(array.starProjectionRuntimeType().getElementType()!!))
    return array
}

@kotlin.clr.ClrIntrinsic("System.Array.CreateInstance")
private fun allocateVector(element: StarProjectionType, size: Int): ProjectedArrayVector = error("CLR binding")

// The caller's ordinary and element witnesses can name a concrete Kotlin
// class and its existential interface respectively. Choose the component
// once, at allocation. In particular, nullable value elements stay boxed.
@PublishedApi
internal fun allocateProjectedArray(ordinary: StarProjectionType, element: StarProjectionType, size: Int): ProjectedArrayVector {
    val exact = ordinary != element && !ordinary.isValueType &&
        (ordinary.isGenericType && runtimeKClass(ordinary) == element ||
            ordinary.isSzArray && element == (ProjectedArrayVector::class as StarProjectionType))
    val result = allocateVector(if (exact) ordinary else element, size)
    prepareKotlinArrayCopy(result)
    return result
}

@PublishedApi
internal fun readProjectedArray(array: ProjectedArrayVector, index: Int): Any? {
    val state = stateOf(array) ?: return array.read(index)
    enterArrayState(state)
    try { return state.overrides?.get(index) ?: array.read(index) }
    finally { exitArrayState(state) }
}

@PublishedApi
internal fun writeProjectedArray(array: ProjectedArrayVector, index: Int, value: Any?) {
    val state = stateOf(array)
    if (state == null) {
        // Array.SetValue performs numeric widening and defaults null to zero
        // for non-nullable value types. Neither is a Kotlin array store.
        val element = array.starProjectionRuntimeType().getElementType()!!
        if (if (value == null) !starProjectionTypeAcceptsNull(element) else !element.acceptsArrayValue(value))
            throw ArrayComponentMismatch()
        if (element.isRowComponent() && value is ProjectedArrayVector) {
            if (index < 0 || index >= array.length) throw IndexOutOfBoundsException()
            exposeProjectedArray(value)
        }
        array.write(value, index)
        return
    }
    // A row store can publish another vector while its parent is locked.
    // Use the same lock order as a multi-vector publication.
    val rowStore = state.element.isRowComponent() && value is ProjectedArrayVector
    if (rowStore) enterArrayState(projectedArrayBatchGate)
    try {
        enterArrayState(state)
        try {
            if (index < 0 || index >= array.length) throw IndexOutOfBoundsException()
            if (value == null || state.element.isAssignableFrom(value.starProjectionRuntimeType())) {
                if (rowStore && (state.exported || state.addressed?.contains(index) == true))
                    exposeProjectedArray(value as ProjectedArrayVector)
                array.write(value, index)
                state.overrides?.remove(index)
            } else {
                if (state.exported || state.addressed?.contains(index) == true)
                    throw ClassCastException("Incompatible element in an exported CLR array location")
                if (!logicalComponentAccepts(state.element, value.starProjectionRuntimeType()))
                    throw ArrayComponentMismatch()
                val slots = state.overrides ?: mutableMapOf<Int, Any>().also { state.overrides = it }
                slots[index] = value
                // Do not keep the displaced element alive through the native vector.
                array.write(null, index)
            }
        } finally { exitArrayState(state) }
    } finally { if (rowStore) exitArrayState(projectedArrayBatchGate) }
}

// Called after checking the actual native destination type. Only a successful
// export seals the real vector; a later compatible Kotlin store can repair a
// rejected export. Never replace the vector or copy its elements at a boundary.
@PublishedApi
internal fun exposeProjectedArray(array: ProjectedArrayVector?): ProjectedArrayVector? {
    if (array == null) return null
    if (array.hasArrayRows()) {
        exposeProjectedArrays(arrayOf<Any?>(array))
        return array
    }
    val state = stateOf(array) ?: return array
    enterArrayState(state)
    try {
        if (state.overrides?.isNotEmpty() == true) throw ClassCastException("Incompatible elements in a CLR array")
        state.exported = true
        return array
    } finally { exitArrayState(state) }
}

private val projectedArrayBatchGate = Any()

private fun StarProjectionType.isRowComponent(): Boolean =
    isArray || this == (ProjectedArrayVector::class as StarProjectionType)

private fun ProjectedArrayVector.hasArrayRows(): Boolean {
    val type = starProjectionRuntimeType()
    return type.isSzArray && type.getElementType()?.isRowComponent() == true
}

@PublishedApi
internal fun exposeProjectedValue(value: Any?) {
    if (value is ProjectedArrayVector) exposeProjectedArray(value)
}

// Multiple operands are published by one call. Keep all participating states
// locked from validation through commit. Only a batch acquires more than one
// state; serializing that acquisition also handles reversed and aliased inputs.
@PublishedApi
internal fun exposeProjectedArrays(arrays: Array<Any?>) {
    publishProjectedArrays(arrays, null, null)
}

@PublishedApi
internal fun exposeProjectedArguments(arrays: Array<Any?>, indexedArrays: Array<Any?>, indices: IntArray) {
    publishProjectedArrays(arrays, indexedArrays, indices)
}

private fun publishProjectedArrays(arrays: Array<Any?>, indexedArrays: Array<Any?>?, indices: IntArray?) {
    val states = mutableListOf<ProjectedArrayState>()
    val pending = mutableListOf<ProjectedArrayVector>()
    val seen = mutableSetOf<ProjectedArrayVector>()
    val whole = mutableListOf<ProjectedArrayState>()
    for (operand in arrays) if (operand is ProjectedArrayVector) pending.add(operand)
    var acquired = 0
    enterArrayState(projectedArrayBatchGate)
    try {
        if (indexedArrays != null && indices != null) {
            var i = 0
            while (i < indexedArrays.size) {
                val state = stateOf(indexedArrays[i] as ProjectedArrayVector)
                if (state != null) {
                    states.add(state)
                    enterArrayState(state)
                    acquired++
                    if (state.overrides?.containsKey(indices[i]) == true)
                        throw ClassCastException("CLR managed reference requires a representable array element")
                }
                i++
            }
        }
        var next = 0
        while (next < pending.size) {
            val array = pending[next++]
            if (!seen.add(array)) continue
            val state = stateOf(array)
            if (state != null) {
                states.add(state)
                enterArrayState(state)
                acquired++
                if (state.overrides?.isNotEmpty() == true)
                    throw ClassCastException("Incompatible elements in a CLR array")
                whole.add(state)
            }
            // A CLR jagged array exposes each row by identity, even when
            // Kotlin never passed that row as a separate call operand.
            if (array.hasArrayRows()) {
                var i = 0
                while (i < array.length) {
                    val row = array.read(i++)
                    if (row is ProjectedArrayVector) pending.add(row)
                }
            }
        }
        for (state in whole) state.exported = true
        if (indexedArrays != null && indices != null) {
            var i = 0
            while (i < indexedArrays.size) {
                val state = stateOf(indexedArrays[i] as ProjectedArrayVector)
                if (state != null && !state.exported) {
                    val addressed = state.addressed ?: mutableSetOf<Int>().also { state.addressed = it }
                    addressed.add(indices[i])
                }
                i++
            }
        }
    } finally {
        while (acquired > 0) exitArrayState(states[--acquired])
        exitArrayState(projectedArrayBatchGate)
    }
}

// A fresh Kotlin allocation does not inherit a foreign source's publication
// state. Its actual component determines whether erased writes are possible.
internal fun prepareKotlinArrayCopy(destination: Any) {
    val vector = destination as ProjectedArrayVector
    val component = vector.starProjectionRuntimeType().getElementType()!!
    if (component.isRowComponent() || component.isGenericType && runtimeKClass(component) != component)
        registerProjectedArray(vector)
}

private class ProjectedSpread { val values = mutableListOf<Any?>() }

@PublishedApi
internal fun newProjectedSpread(): Any = ProjectedSpread()

// Consume each spread immediately: a later operand can mutate its source.
@PublishedApi
internal fun appendProjectedSpread(builder: Any, value: Any?, spread: Boolean) {
    val values = (builder as ProjectedSpread).values
    if (!spread) { values.add(value); return }
    val source = value as ProjectedArrayVector
    var i = 0
    while (i < source.length) values.add(readProjectedArray(source, i++))
}

@PublishedApi
internal fun projectedSpreadSize(builder: Any): Int = (builder as ProjectedSpread).values.size

@PublishedApi
internal fun finishProjectedSpread(builder: Any, destination: ProjectedArrayVector): ProjectedArrayVector {
    if (stateOf(destination) == null) prepareKotlinArrayCopy(destination)
    val values = (builder as ProjectedSpread).values
    var i = 0
    while (i < values.size) { writeProjectedArray(destination, i, values[i]); i++ }
    return destination
}

internal fun copyProjectedArrayRange(source: Any, from: Int, destination: Any, to: Int, count: Int): Boolean {
    val src = source as ProjectedArrayVector
    val dst = destination as ProjectedArrayVector
    if (stateOf(src) == null && stateOf(dst) == null) return false
    // Observe logical elements, including pending incompatible stores. Keep
    // memmove semantics without locking two arrays in opposite orders.
    if (source === destination && to > from && to - from < count) {
        var i = count
        while (i > 0) {
            i--
            writeProjectedArray(dst, to + i, readProjectedArray(src, from + i))
        }
    } else {
        var i = 0
        while (i < count) {
            writeProjectedArray(dst, to + i, readProjectedArray(src, from + i))
            i++
        }
    }
    return true
}
