package roundtrip.mutablepolicy

class PortableMutableCollection<T>(private val values: MutableList<T>) : AbstractMutableCollection<T>() {
    var iterations: Int = 0
    var failIteration: Boolean = false
    override val size: Int get() = values.size
    override fun add(element: T): Boolean = values.add(element)
    override fun iterator(): MutableIterator<T> {
        iterations++
        if (failIteration) throw IllegalStateException("iteration")
        return values.iterator()
    }
}

class PortableRenderingElement(val value: Int) {
    var calls: Int = 0
    var failRendering: Boolean = false
    override fun toString(): String {
        calls++
        if (failRendering) throw IllegalArgumentException("element")
        return "value:$value"
    }
}

class PortableAuthoredRenderList<T>(private val values: MutableList<T>) : AbstractMutableList<T>() {
    override val size: Int get() = values.size
    override fun get(index: Int): T = values[index]
    override fun set(index: Int, element: T): T = values.set(index, element)
    override fun add(index: Int, element: T) = values.add(index, element)
    override fun removeAt(index: Int): T = values.removeAt(index)
    override fun toString(): String = "authored"
}

class PortableSuperRenderList<T>(private val values: MutableList<T>) : AbstractMutableList<T>() {
    override val size: Int get() = values.size
    override fun get(index: Int): T = values[index]
    override fun set(index: Int, element: T): T = values.set(index, element)
    override fun add(index: Int, element: T) = values.add(index, element)
    override fun removeAt(index: Int): T = values.removeAt(index)
    override fun toString(): String = "super:" + super.toString()
}

fun renderMutableThroughAny(value: Any): String = value.toString()
fun <T> renderMutableGeneric(value: T): String = value.toString()
