import NUnit.Framework.TestAttribute
import GenericValueInterop.VisitExtension
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

private typealias WideCallback<R> = (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> R
private typealias WideIntCallback = WideCallback<Int>
private typealias BoxedCallback = (Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any) -> Any?
private class RetainedCallback(val saved: BoxedCallback) : GenericValueInterop.NativeCallbackBase(saved) {
    override fun MakeCallback(): BoxedCallback = saved
}
private typealias WideFactory = (Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any) -> WideIntCallback
private fun nativeFactory(): WideFactory = GenericValueInterop.NativeWideCallbacks.Factory
private suspend fun <T : GenericValueInterop.ILeftMark> stagedFactory(tag: T, pause: suspend () -> Unit): Int {
    pause()
    val factory = GenericValueInterop.NativeWideCallbacks.SelectFactory<T>(tag)
    val inner = factory(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)
    return inner(GenericValueInterop.MarkedValue<T> { tag }, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)
}
private class DerivedCallbackScope : GenericValueInterop.NativeCallbackScope() {
    fun visitStored(f: WideCallback<Unit>) = Visit<String>(f)
}
private class ValueVisitor(val f: WideCallback<Unit>) : GenericValueInterop.NativeVisitor() {
    override fun <T : GenericValueInterop.IVisitWide> Accept(value: T): Boolean = value.Apply(f)
}
private class UnitRecorder : WideCallback<Unit> {
    var sum = 0
    override fun invoke(a: Int, b: Int, c: Int, d: Int, e: Int, f: Int, g: Int, h: Int, i: Int,
                        j: Int, k: Int, l: Int, m: Int, n: Int, o: Int, p: Int, q: Int) { sum += a + q }
}
private class FirstPlusLast : WideIntCallback {
    override fun invoke(a: Int, b: Int, c: Int, d: Int, e: Int, f: Int, g: Int, h: Int, i: Int,
                        j: Int, k: Int, l: Int, m: Int, n: Int, o: Int, p: Int, q: Int): Int = a + q
}
private class NativeIntCallbackDerived(op: FirstPlusLast) : GenericValueInterop.NativeIntCallbackBase(op)
private class NativeFunctionDerived(op: WideIntCallback) : GenericValueInterop.NativeIntCallbackBase(op)
private fun constructWithStoredFunction(op: WideIntCallback) = GenericValueInterop.NativeCallbackOwner<Int>(op).Read(1)
private class CallbackFactory : WideCallback<WideIntCallback> {
    override fun invoke(a: Int, b: Int, c: Int, d: Int, e: Int, f: Int, g: Int, h: Int, i: Int,
                        j: Int, k: Int, l: Int, m: Int, n: Int, o: Int, p: Int, q: Int): WideIntCallback =
        { first, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, last -> a + q + first + last }
}
private class NestedCallbackDerived(op: CallbackFactory) : GenericValueInterop.NestedCallbackBase(op)
private class NativeGetterDerived : GenericValueInterop.NativeCallbackBase(GenericValueInterop.NativeWideCallbacks.IntResult)
private class NativeArrayDerived(op: (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Any)
    : GenericValueInterop.NativeArrayCallbackBase(op)
private class CallbackBox<T>(val value: T)
fun returnedCallback(source: GenericValueInterop.NativeIntCallbackBase): WideIntCallback = source.Callback
private fun stagedCallback(source: GenericValueInterop.NativeIntCallbackBase, tag: Any): WideIntCallback? =
    source.ReadCallback(tag) as? WideIntCallback
private fun invokeWithRange(f: WideIntCallback) = f(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)

class WideFunctionTypeTests {
    @TestAttribute
    fun sameFunctionRemovesOneRegistrationAcrossSeparateClrConversions() {
        var calls = 0
        val f: WideIntCallback = { a, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, q -> calls++; a + q }
        val owner = GenericValueInterop.NativeIntCallbackBase(f)
        owner.Register(f)
        owner.Register(f)
        check(owner.Raise() == 18 && calls == 2)
        owner.Unregister(f)
        check(owner.Raise() == 18 && calls == 3)
        owner.Unregister(f)
        check(owner.Raise() == -1 && calls == 3)
    }

    @TestAttribute
    fun retainedCallbackPublishesItsResultOnlyAtNativeInvocation() {
        val initial = CallbackBox("initial")
        var result = arrayOf(initial)
        var visits = 0
        val callback: BoxedCallback = { _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _ -> visits++; result }
        val native = GenericValueInterop.NativeCallbackBase(callback)
        val derived = RetainedCallback(callback)
        val fromNative: BoxedCallback = GenericValueInterop.CallbackArrayProbe().Inspect
        check(GenericValueInterop.NativeCallbackBase(fromNative).CallbackIdentity === fromNative)
        check(native.CallbackIdentity === derived.CallbackIdentity)
        check(visits == 0 && native.Read() === result && native.ReadFirstElement() === initial)
        result = arrayOf(CallbackBox("next"))
        @Suppress("UNCHECKED_CAST")
        val incompatible = CallbackBox(7) as CallbackBox<String>
        result[0] = incompatible
        try { native.ReadFirstElement(); error("C# received an incompatible array") }
        catch (_: System.InvalidCastException) {}
        try { derived.ReadFirstElement(); error("C# base received an incompatible array") }
        catch (_: System.InvalidCastException) {}
        derived.Stored = callback
        derived.Field = callback
        for (slot in 0..2) {
            try { derived.InvokeStored(slot); error("C# received an incompatible callback result from slot $slot") }
            catch (_: System.InvalidCastException) {}
        }
        val local = callback(1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17) as Array<*>
        check(visits == 8 && local === result && local[0] === incompatible)
        val token = derived.Changed.subscribe(callback)
        try {
            try { derived.Raise(); error("event received a stale result") } catch (_: System.InvalidCastException) {}
            try { GenericValueInterop.NativeCallbackBase.ReadErased(callback); error("erased delegate received a stale result") }
            catch (_: System.InvalidCastException) {}
        } finally { token.close() }
        var laterCalls = 0
        val later: BoxedCallback = { _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _ -> laterCalls++; result }
        val multicast = GenericValueInterop.CallbackArrayProbe.Combine(callback, later)
        try { GenericValueInterop.NativeCallbackBase.ReadErased(multicast); error("first invalid return was ignored") }
        catch (_: System.InvalidCastException) {}
        check(laterCalls == 0)
        @Suppress("UNCHECKED_CAST")
        val kotlinChain = multicast as BoxedCallback
        val last = kotlinChain(1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17) as Array<*>
        check(laterCalls == 1 && last === result && last[0] === incompatible)
        for (restored in listOf(derived.MakeCallback(), derived.Stored)) {
            val value = restored!!(1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17) as Array<*>
            check(value === result && value[0] === incompatible)
        }
    }

    @TestAttribute
    fun onlyNativeCallbacksPublishArrayArguments() {
        fun call(f: BoxedCallback, x: Any) =
            f(x, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)

        val probe = GenericValueInterop.CallbackArrayProbe()
        val native: BoxedCallback = probe.Inspect
        val good = arrayOf(CallbackBox("ok"))
        check(call(native, good) === good)

        val shadow = arrayOf(CallbackBox("before"))
        @Suppress("UNCHECKED_CAST")
        val wrong = CallbackBox(42) as CallbackBox<String>
        shadow[0] = wrong
        var visits = 0
        val local: BoxedCallback = { a, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _ -> visits++; (a as Array<*>)[0] }
        check(call(local, shadow) === wrong)
        @Suppress("UNCHECKED_CAST")
        fun combine(first: BoxedCallback, second: BoxedCallback) =
            GenericValueInterop.CallbackArrayProbe.Combine(first, second) as BoxedCallback
        check(call(combine(local, local), shadow) === wrong && visits == 3)
        try { call(combine(local, native), shadow); error("mixed callback entered a handler") }
        catch (_: System.InvalidCastException) {}
        check(visits == 3)
        try { call(native, shadow); error("native body received an incompatible array") }
        catch (_: System.InvalidCastException) {}
        assertEquals(1, probe.Calls)
    }

    @TestAttribute
    fun nativeValueAndVoidCallbacksInvokeTheOriginalRecorder() {
        val recorder = UnitRecorder()
        check(GenericValueInterop.NativeWideCallbacks.VisitUnit(recorder))
        assertEquals(18, recorder.sum)
        GenericValueInterop.NativeWideCallbacks.VisitVoid(recorder)
        assertEquals(36, recorder.sum)
        assertEquals(71, DerivedCallbackScope().visitStored(recorder))
        assertEquals(54, recorder.sum)
        val target = GenericValueInterop.NativeCallbackOverride()
        assertEquals(19, target.Choose(recorder))
        check(target.VisitExtension(recorder))
        val visit: (WideCallback<Unit>) -> Boolean = GenericValueInterop.NativeWideCallbacks::VisitUnit
        check(visit(recorder))
        check(ValueVisitor(recorder).Run())
        assertEquals(126, recorder.sum)
    }

    @TestAttribute
    fun wideFunctionValuesCrossTheModuleBoundary() {
        assertEquals(17, NativeCallbackDerived(LastArgument()).Read())
        val native = NativeIntCallbackDerived(FirstPlusLast())
        assertEquals(18, native.Read())
        native.Callback = FirstPlusLast()
        assertEquals(18, native.Read())
        assertEquals(18, NativeFunctionDerived(FirstPlusLast()).Read())
        assertEquals(18, constructWithStoredFunction(FirstPlusLast()))
        assertEquals(18, GenericValueInterop.NativeWideCallbacks.Read(FirstPlusLast()))
        assertEquals(18, GenericValueInterop.NativeWideCallbacks.Read<String>(FirstPlusLast()))
        assertEquals(18, NativeGetterDerived().Read())
        val before = native.CallbackReads
        assertEquals(18, invokeWithRange(returnedCallback(native)))
        assertEquals(18, invokeWithRange(stagedCallback(native, "tag")!!))
        native.Callback = null
        try { returnedCallback(native); error("null callback escaped the non-null return") }
        catch (_: NullPointerException) {}
        assertEquals(before + 3, native.CallbackReads)
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
    fun returnedFunctionsAreCallableOnBothSidesOfTheNativeBoundary() {
        fun invokeFactory(factory: WideFactory) = invokeWithRange(factory(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17))
        assertEquals(-32, invokeFactory(nativeFactory()))
        assertEquals(-32, invokeWithRange(GenericValueInterop.NativeWideCallbacks.Factory(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)))
        val erased: Any? = GenericValueInterop.NativeWideCallbacks.Factory
        @Suppress("UNCHECKED_CAST")
        assertEquals(-32, invokeFactory(erased as WideFactory))
        assertEquals(42, runCrossModuleSuspend { stagedFactory(GenericValueInterop.SharedNativeStream<Int>()) {} })
        val factory = CallbackFactory()
        assertEquals(36, NestedCallbackDerived(factory).Read())
        val stored: WideCallback<WideIntCallback> = factory
        assertEquals(36, GenericValueInterop.NativeWideCallbacks.ReadNested(stored))
        assertEquals(18, GenericValueInterop.NativeWideCallbacks.ReadNested { _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _ ->
            FirstPlusLast()
        })
        var calls = 0
        val make: () -> WideIntCallback = { calls++; FirstPlusLast() }
        assertEquals(18, GenericValueInterop.NativeWideCallbacks.ReadFactory(GenericValueInterop.WideFactory(make)))
        assertEquals(1, calls)
    }

    @TestAttribute
    fun wideFunctionVarianceMatchesKotlinFunctionTypes() {
        val source: (Any, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> String = narrowSource()
        val widened: (String, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Any = source
        assertEquals("s17", widened("s", 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17))
        assertEquals("s17", acceptWidened(source))
    }
}
