package roundtriptests.nullableinvariantflow

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.nullableinvariantflow.*
import kotlin.clr.byref
import kotlin.coroutines.*

private class NullableCompletion<T> : Continuation<T> {
    var outcome: Result<T>? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<T>) { outcome = result }
}
private fun <T> runNullableSuspend(action: suspend () -> T): T {
    val completion = NullableCompletion<T>()
    action.startCoroutine(completion)
    return completion.outcome!!.getOrThrow()
}
private fun <T> resumeNullableAfterPause(box: Box<T?>): Box<T?> {
    val completion = NullableCompletion<Box<T?>>()
    var pending: Continuation<Unit>? = null
    val operation: suspend () -> Box<T?> = {
        nullableAfterPause(box) { suspendCoroutine<Unit> { pending = it } }
    }
    operation.startCoroutine(completion)
    assertTrue(completion.outcome == null)
    pending!!.resume(Unit)
    return completion.outcome!!.getOrThrow()
}
private class ConsumerNullableOwner<T>(val box: Box<T?>) {
    inner class View<U>(val other: U) {
        fun read(helper: UnframedHelper<T>): T = helper.value
    }
}

private fun <T> inlineNullableEarlyExit(): Int {
    InlineNullableBody().isAbsent<T>(null) { return 17 }
    return -1
}

class NullableInvariantFlowTests {
    @TestAttribute
    fun reifiedDelegateAcceptsNullableString() {
        assertTrue(nullableTypePredicate<String?>()(null))
    }
    @TestAttribute
    fun reifiedDelegateRejectsNullString() {
        assertTrue(!nullableTypePredicate<String>()(null))
    }
    @TestAttribute
    fun reifiedDelegateAcceptsNullableInt() {
        assertTrue(nullableTypePredicate<Int?>()(null))
    }
    @TestAttribute
    fun reifiedDelegateAcceptsBoxedInt() {
        val input: Any? = 42
        assertTrue(nullableTypePredicate<Int>()(input))
    }
    @TestAttribute
    fun separateProducerFilesSelectTheOriginalAbstractOverload() {
        val receiver = StringReceiver()
        assertEquals("box:selected", invokeBoxReceiver(receiver, Box("selected")))
        assertEquals("value:selected", invokeValueReceiver(receiver, "selected"))
        invokeAcceptReceiver(receiver, Box("accepted"))
        assertEquals("accepted", receiver.last)
    }

    @TestAttribute
    fun typeAndMethodBoundsRetainTheirKotlinGenericArguments() {
        val box = Box<String?>("bounded")
        val owner = Bound(box)
        assertTrue(owner.box === box)
        assertEquals("bounded", owner.box.value)
        assertEquals("bounded", readBound(box))
    }

    @TestAttribute
    fun inheritedPropertySlotsShareTheirImplementation() {
        val value = KeyImpl("key")
        val child: KeyChild<String> = value
        val root: KeyRoot<String> = value
        assertEquals("key", value.key)
        assertEquals("key", child.key)
        assertEquals("key", root.key)
        assertEquals(1, root.marker)
        assertEquals(1, readLocalMarker(root))
        val integerRoot: KeyRoot<Int> = KeyImpl(23)
        assertEquals(23, integerRoot.key)
        val nestedInteger = NullableOwner<String>(Box("present")).view(31)
        assertEquals(31, nestedInteger.key)
        assertEquals(1, nestedInteger.marker)
        val nestedString = NullableOwner<Int>(Box(null)).view("captured")
        assertEquals("captured", nestedString.key)
        assertEquals(0, nestedString.marker)
    }

    @TestAttribute
    fun directAndInterfaceOverridesPreserveExistingObjectIdentity() {
        val box = Box<String?>(null)
        val direct = StringExchange()
        val throughInterface: Exchange<String> = direct
        assertTrue(exchangeThroughExtraFrame<Int, Boolean, String>(throughInterface) === direct)
        assertTrue(direct.exchange(box) === box)
        assertTrue(throughInterface.exchange(box) === box)
        assertTrue(sameModule(box) === box)
        assertTrue(StringExchange().exchange(Box<String?>(null)).value == null)
        val alias = throughInterface.exchange(box)
        alias.value = "changed"
        assertEquals("changed", box.value)
        box.value = null
        assertTrue(alias.value == null)
    }

    @TestAttribute
    fun genericFunctionsPreserveAliasesAndNullablePayloads() {
        val strings = Box<String?>(null)
        val propertyChild = NullablePropertyChild<String>(strings)
        val nextStrings = Box<String?>("next")
        assertTrue(propertyChild.exchange(nextStrings) === strings)
        assertTrue(propertyChild.exchange(strings) === nextStrings)
        val initialInteger = Box<Int?>(null)
        val integerPropertyChild = NullablePropertyChild<Int>(initialInteger)
        val nextInteger = Box<Int?>(42)
        assertTrue(integerPropertyChild.exchange(nextInteger) === initialInteger)
        assertTrue(integerPropertyChild.exchange(initialInteger) === nextInteger)
        val inlineBody = InlineNullableBody()
        var observed = 0
        assertTrue(inlineBody.isAbsent<String>(null) { observed++ })
        assertTrue(!inlineBody.isAbsent<Int>(42) { observed++ })
        assertEquals(2, observed)
        assertEquals(17, inlineNullableEarlyExit<String>())
        assertEquals(17, inlineNullableEarlyExit<Int>())
        assertTrue(nullableFactory<String>()().value == null)
        assertTrue(nullableFactory<Int>()().value == null)
        assertTrue(inlineNullableFactory<String>()().value == null)
        assertTrue(inlineNullableFactory<Int>()().value == null)
        assertTrue(runNullableSuspend(nullableDeferred<String>(strings)) === strings)
        assertTrue(runNullableSuspend { nullableSuspendEcho<String>(strings) } === strings)
        assertTrue(runNullableSuspend(nullableDeferred<Int>(nextInteger)) === nextInteger)
        assertTrue(runNullableSuspend { nullableSuspendEcho<Int>(nextInteger) } === nextInteger)
        assertTrue(resumeNullableAfterPause<String>(strings) === strings)
        assertTrue(resumeNullableAfterPause<Int>(nextInteger) === nextInteger)
        assertTrue(scalarFromNullableBox<String>(strings) == null)
        assertEquals("next", scalarFromNullableBox<String>(nextStrings))
        assertEquals(42, scalarFromNullableBox<Int>(nextInteger))
        assertTrue(NullableScalarHolder<String>(strings).scalar() == null)
        assertEquals("next", NullableScalarHolder<String>(nextStrings).scalar())
        assertTrue(NullableScalarHolder<Int>(initialInteger).scalar() == null)
        assertEquals(42, NullableScalarHolder<Int>(nextInteger).scalar())
        assertEquals("helper", ConsumerNullableOwner<String>(strings).View(12).read(UnframedHelper("helper")))
        assertEquals(23, ConsumerNullableOwner<Int>(nextInteger).View("other").read(UnframedHelper(23)))
        val bodyDefault = InheritedNullableBodyDefault()
        val bodyDefaultSlot: NullableBodyDefault = bodyDefault
        assertTrue(bodyDefaultSlot.isAbsent<String>(null))
        assertTrue(!bodyDefaultSlot.isAbsent<String>("present"))
        assertTrue(bodyDefault.isAbsent<Int>(null))
        assertTrue(!bodyDefault.isAbsent<Int>(0))
        val suspendBody: NullableSuspendBodySlot = NullableSuspendBodyImpl()
        assertTrue(runNullableSuspend { suspendBody.isAbsent<String>(null) })
        assertTrue(!runNullableSuspend { suspendBody.isAbsent<Int>(42) })
        val inheritedDefault = InheritedNullableDefault()
        val constrainedDefault = InheritedNullableConstrainedDefault()
        assertTrue(constrainedDefault.constrainedIdentity<String, Box<String?>>(strings) === strings)
        val constrainedIntegers = Box<Int?>(42)
        assertTrue(constrainedDefault.constrainedIdentity<Int, Box<Int?>>(constrainedIntegers) === constrainedIntegers)
        val genericChild = NullableGenericChild()
        val genericParent: NullableGenericParent = genericChild
        val genericSlot: NullableGenericSlot = genericChild
        assertTrue(genericChild.echoNullable<String>(strings) === strings)
        assertTrue(genericParent.echoNullable<Int>(constrainedIntegers) === constrainedIntegers)
        assertTrue(genericSlot.echoNullable<String>(strings) === strings)
        assertTrue(inheritedDefault.defaultIdentity<String>(strings) === strings)
        val defaultSlot: NullableDefault = inheritedDefault
        val defaultIntegers = Box<Int?>(null)
        assertTrue(defaultSlot.defaultIdentity<Int>(defaultIntegers) === defaultIntegers)
        val alias = identity(nullableIdentity<String>(strings))
        assertTrue(alias === strings)
        replace<String>(alias, "present")
        assertEquals("present", strings.value)
        replace<String>(strings, null)
        assertTrue(alias.value == null)
        assertEquals("created", create<String>("created").value)
        assertTrue(create<String>(null).value == null)
        assertEquals(42, create<Int>(42).value)
        assertTrue(create<Int>(null).value == null)
        assertTrue(throughInlineNullableFrame<String>(strings) === strings)
        val inlineIntegers = Box<Int?>(42)
        assertTrue(throughInlineNullableFrame<Int>(inlineIntegers) === inlineIntegers)
        assertEquals(42, inlineIntegers.value)
        assertTrue(inlineNullableTransform<Boolean, String, Int>(inlineIntegers) { null } === inlineIntegers)
        assertTrue(inlineIntegers.value == null)
        assertTrue(inlineNullableTransform<Int, Boolean, String>(strings) { "inline" } === strings)
        assertEquals("inline", strings.value)
        val stringArray = arrayOf<String?>("before")
        clearNullableElement<String>(stringArray)
        assertTrue(stringArray[0] == null)
        val integerArray = arrayOf<Int?>(42)
        clearNullableElement<Int>(integerArray)
        assertTrue(integerArray[0] == null)
        assertEquals("captured", nullableSupplier<String>("captured")())
        assertTrue(nullableSupplier<String>(null)() == null)
        assertEquals(42, nullableSupplier<Int>(42)())
        assertTrue(nullableSupplier<Int>(null)() == null)
        val stringOwnerBody = NullableOwnerBody<String>()
        assertTrue(stringOwnerBody.isAbsent(null))
        assertTrue(!stringOwnerBody.isAbsent("present"))
        val integerOwnerBody = NullableOwnerBody<Int>()
        assertTrue(integerOwnerBody.isAbsent(null))
        assertTrue(!integerOwnerBody.isAbsent(0))
        assertTrue(nullableBodyOnly<String>(null))
        assertTrue(!nullableBodyOnly<String>("present"))
        assertTrue(forwardNullableBodyOnly<Int>(null))
        assertTrue(!forwardNullableBodyOnly<Int>(42))
        val throughSlot: NullableBodySlot = NullableBodyImplementation()
        assertTrue(throughSlot.isAbsent<String>(null))
        assertTrue(!throughSlot.isAbsent<String>("present"))
        assertTrue(throughSlot.isAbsent<Int>(null))
        assertTrue(!throughSlot.isAbsent<Int>(42))
        assertTrue(throughSlot.bothAbsent<String, Int>(null, null))
        assertTrue(!throughSlot.bothAbsent<String, Int>(null, 42))
        assertTrue(!throughSlot.bothAbsent<Int, String>(42, null))
        var integer = 1
        assertTrue(throughSlot.writeAndObserve(42, byref(integer), byref(integer)))
        assertEquals(42, integer)
        var text = "before"
        assertTrue(throughSlot.writeAndObserve("after", byref(text), byref(text)))
        assertEquals("after", text)
    }

    @TestAttribute
    fun ordinaryInvariantValuesRetainTypedReadsAndWrites() {
        val integers = Box(10)
        val alias = identity(integers)
        assertTrue(alias === integers)
        alias.value = alias.value + 7
        assertEquals(17, integers.value)
        val strings = Box("before")
        identity(strings).value = "after"
        assertEquals("after", strings.value)
    }

    @TestAttribute
    fun nestedGenericAndArrayStorageKeepTheSameValues() {
        val inner = create<String>("before")
        val outer = Box(inner)
        val array = arrayOf(outer)
        assertTrue(identity(array[0]).value === inner)
        replace<String>(array[0].value, "after")
        assertEquals("after", outer.value.value)
        val replacement = create<String>(null)
        array[0].value = replacement
        assertTrue(outer.value === replacement)
        assertTrue(outer.value.value == null)
    }
}
