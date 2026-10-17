import NUnit.Framework.TestAttribute
import kotlin.clr.byref

private class NativeConstraintHolder<T : OutRef.NativeBase>(var value: OutRef.NativeBound<T>? = null)
private class NativeChild(value: Int) : OutRef.NativeBase(value)
private class NativeReader : OutRef.NativeApi() {
    override fun Read(value: OutRef.NativeBase): Int = value.Value
}
private class NativeNumericValue(private val n: Int) : Number() {
    override fun toByte(): Byte = n.toByte()
    override fun toShort(): Short = n.toShort()
    override fun toInt(): Int = n
    override fun toLong(): Long = n.toLong()
    override fun toFloat(): Float = n.toFloat()
    override fun toDouble(): Double = n.toDouble()
}

class NumberNativeTests {
    @TestAttribute
    fun nativeConstraintsAndManagedReferencesRemainExact() {
        val holder = NativeConstraintHolder<OutRef.NativeBase>()
        check(holder.value == null)
        val native = NativeChild(41)
        check(OutRef.NativeApi.Invoke(NativeReader(), native) == 41)
        holder.value = OutRef.NativeBound(native)
        var read = OutRef.NativeBase(0)
        check(OutRef.NativeSlots.Read(holder.value!!, byref(read)))
        check(read === native)
        var numberBox = OutRef.ReferenceBox(NativeNumericValue(1))
        val replacement = OutRef.ReferenceBox(NativeNumericValue(42))
        OutRef.NativeSlots.Replace(byref(numberBox), replacement)
        check(numberBox === replacement)
        check((numberBox.Value as Number).toInt() == 42)
    }
}
