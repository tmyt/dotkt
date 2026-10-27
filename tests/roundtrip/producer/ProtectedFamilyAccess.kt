package roundtrip.protectedfamily

import kotlin.clr.ClrField

class FamilyCaptureBox<T>(val value: T)

open class FamilyBase<T>(val payload: T) {
    protected var index = 17
    @ClrField protected var raw = 19
    protected open fun read(): Int = index + raw
    protected fun transform(block: (Int) -> Int): Int = block(index)
    protected val callback: () -> Int get() = { index }
    protected fun stable(): Int = index + raw
    protected fun <X> takeBox(box: FamilyCaptureBox<X>): Any? = box.value
    fun virtualRead(): Int = read()
    protected inline fun capturedInline(crossinline touch: () -> Unit): () -> Int = { touch(); index + raw }
}

open class FamilyDerived<T>(value: T) : FamilyBase<T>(value) {
    fun ordinary(): Int { index++; raw++; return index + raw }
    fun alias(): Int { val self = this; return self.index + self.raw }
    fun captured(): () -> Int { val delta = 1; return { index + raw + delta } }
    fun bound(): () -> Int = this::read
    fun inheritedBound(): () -> Int = this::stable
    fun <U> across(other: FamilyDerived<U>): Int = other.index + other.raw
    fun starCall(box: FamilyCaptureBox<*>): Any? = takeBox(box)
    fun aliasedStarCall(box: FamilyCaptureBox<*>): Any? { val self = this; return self.takeBox(box) }
    fun capturedStarCall(box: FamilyCaptureBox<*>): () -> Any? {
        val self = this
        return { self.takeBox(box) }
    }
    fun <D : FamilyDerived<T>> viaBound(other: D): Int = other.index + other.raw
    fun functions(): Int = transform { it + raw } + callback()
    override fun read(): Int = super.read() + 100
}
