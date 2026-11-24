import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.wide.acceptWidened
import roundtrip.wide.narrowSource
import roundtrip.wide.param17
import roundtrip.wide.ret17
import roundtrip.wide.retAction17

private class LastArgument : (Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any) -> Any {
    override fun invoke(a: Any, b: Any, c: Any, d: Any, e: Any, f: Any, g: Any, h: Any, i: Any,
                        j: Any, k: Any, l: Any, m: Any, n: Any, o: Any, p: Any, q: Any): Any = q
}

private class NativeCallbackDerived(op: LastArgument) : GenericValueInterop.NativeCallbackBase(op)

private typealias WideIntCallback = (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int
private class FirstPlusLast : WideIntCallback {
    override fun invoke(a: Int, b: Int, c: Int, d: Int, e: Int, f: Int, g: Int, h: Int, i: Int,
                        j: Int, k: Int, l: Int, m: Int, n: Int, o: Int, p: Int, q: Int): Int = a + q
}
private class NativeIntCallbackDerived(op: FirstPlusLast) : GenericValueInterop.NativeIntCallbackBase(op)
private class NativeFunctionDerived(op: WideIntCallback) : GenericValueInterop.NativeIntCallbackBase(op)
private class NativeGetterDerived : GenericValueInterop.NativeCallbackBase(GenericValueInterop.NativeWideCallbacks.IntResult)
private class NativeArrayDerived(op: (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Any)
    : GenericValueInterop.NativeArrayCallbackBase(op)
private class CallbackBox<T>(val value: T)

class WideFunctionTypeTests {
    @TestAttribute
    fun wideFunctionValuesCrossTheModuleBoundary() {
        assertEquals(17, NativeCallbackDerived(LastArgument()).Read())
        val native = NativeIntCallbackDerived(FirstPlusLast())
        assertEquals(18, native.Read())
        native.Callback = FirstPlusLast()
        assertEquals(18, native.Read())
        assertEquals(18, NativeFunctionDerived(FirstPlusLast()).Read())
        assertEquals(18, GenericValueInterop.NativeWideCallbacks.Read(FirstPlusLast()))
        assertEquals(18, NativeGetterDerived().Read())
        val array = arrayOf(CallbackBox("text"))
        val returning = NativeArrayDerived { _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _ -> array }
        check(returning.Read() === array)
        @Suppress("UNCHECKED_CAST")
        val incompatible = CallbackBox(7) as CallbackBox<String>
        val unexported = arrayOf(CallbackBox("other"))
        unexported[0] = incompatible
        val invalid = NativeArrayDerived { _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _ -> unexported }
        try { invalid.Read(); error("native callback exposed an incompatible array") }
        catch (_: System.InvalidCastException) {}
        assertEquals(18, param17 { p1, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, p17 -> p1 + p17 })
        assertEquals(18, ret17()(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17))

        var seen = 0
        retAction17 { seen = it }(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)
        assertEquals(18, seen)
    }

    @TestAttribute
    fun wideFunctionVarianceMatchesKotlinFunctionTypes() {
        val source: (Any, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> String = narrowSource()
        val widened: (String, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Any = source
        assertEquals("s17", widened("s", 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17))
        assertEquals("s17", acceptWidened(source))
    }
}
