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
    @kotlin.clr.ClrIntrinsic("TryGetValue") fun find(key: K, value: ClrRef<V?>): Boolean = error("CLR binding")
}

private class ProjectedArrayState(val element: StarProjectionType) {
    var overrides: MutableMap<Int, Any>? = null
    var exported = false
}
private val projectedArrays = ProjectedArrayTable<ProjectedArrayVector, ProjectedArrayState>()

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

// Register exactly once, at allocation of a Kotlin array with a narrower
// physical reference component. Foreign and ordinary CLR arrays are not opted in.
@PublishedApi
internal fun registerProjectedArray(array: ProjectedArrayVector): ProjectedArrayVector {
    projectedArrays.add(array, ProjectedArrayState(array.starProjectionRuntimeType().getElementType()!!))
    return array
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
        array.write(value, index)
        return
    }
    enterArrayState(state)
    try {
        if (index < 0 || index >= array.length) throw IndexOutOfBoundsException()
        if (value == null || state.element.isAssignableFrom(value.starProjectionRuntimeType())) {
            array.write(value, index)
            state.overrides?.remove(index)
        } else {
            if (state.exported) throw ClassCastException("Incompatible element in an exported CLR array")
            val slots = state.overrides ?: mutableMapOf<Int, Any>().also { state.overrides = it }
            slots[index] = value
            // Do not keep the displaced element alive through the native vector.
            array.write(null, index)
        }
    } finally { exitArrayState(state) }
}

// Called after checking the actual native destination type. Only a successful
// export seals the real vector; a later compatible Kotlin store can repair a
// rejected export. Never replace the vector or copy its elements at a boundary.
@PublishedApi
internal fun exposeProjectedArray(array: ProjectedArrayVector?): ProjectedArrayVector? {
    if (array == null) return null
    val state = stateOf(array) ?: return array
    enterArrayState(state)
    try {
        if (state.overrides?.isNotEmpty() == true) throw ClassCastException("Incompatible elements in a CLR array")
        state.exported = true
        return array
    } finally { exitArrayState(state) }
}

private val projectedArrayBatchGate = Any()

@PublishedApi
internal fun exposeProjectedValue(value: Any?) {
    if (value is ProjectedArrayVector) exposeProjectedArray(value)
}

// Multiple operands are published by one call. Keep all participating states
// locked from validation through commit. Only a batch acquires more than one
// state; serializing that acquisition also handles reversed and aliased inputs.
@PublishedApi
internal fun exposeProjectedArrays(arrays: Array<Any?>) {
    val states = arrayOfNulls<ProjectedArrayState>(arrays.size)
    enterArrayState(projectedArrayBatchGate)
    var acquired = 0
    try {
        while (acquired < arrays.size) {
            val array = arrays[acquired] as? ProjectedArrayVector
            val state = if (array == null) null else stateOf(array)
            states[acquired] = state
            if (state != null) enterArrayState(state)
            acquired++
        }
        for (state in states) if (state?.overrides?.isNotEmpty() == true)
            throw ClassCastException("Incompatible elements in a CLR array")
        for (state in states) if (state != null) state.exported = true
    } finally {
        while (acquired > 0) {
            val state = states[--acquired]
            if (state != null) exitArrayState(state)
        }
        exitArrayState(projectedArrayBatchGate)
    }
}

// A fresh Kotlin copy inherits the source's writable representation, not its
// publication state. The concrete vector has already been allocated by its
// component-aware factory.
internal fun inheritProjectedArrayStorage(source: Any, destination: Any) {
    if (stateOf(source as ProjectedArrayVector) != null)
        registerProjectedArray(destination as ProjectedArrayVector)
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
