package roundtrip.protectedfamily

import kotlin.clr.ClrField

open class FamilyBase<T>(val payload: T) {
    protected var index = 17
    @ClrField protected var raw = 19
    protected open fun read(): Int = index + raw
    protected fun transform(block: (Int) -> Int): Int = block(index)
    protected val callback: () -> Int get() = { index }
    fun virtualRead(): Int = read()
    protected inline fun capturedInline(crossinline touch: () -> Unit): () -> Int = { touch(); index + raw }
}

open class FamilyDerived<T>(value: T) : FamilyBase<T>(value) {
    fun ordinary(): Int { index++; raw++; return index + raw }
    fun alias(): Int { val self = this; return self.index + self.raw }
    fun captured(): () -> Int { val delta = 1; return { index + raw + delta } }
    fun bound(): () -> Int = this::read
    fun <D : FamilyDerived<T>> viaBound(other: D): Int = other.index + other.raw
    fun functions(): Int = transform { it + raw } + callback()
    override fun read(): Int = super.read() + 100
}
