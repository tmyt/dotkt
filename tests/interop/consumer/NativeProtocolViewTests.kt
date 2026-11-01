import NUnit.Framework.TestAttribute
import NativeProtocolViews.Counter
import NativeProtocolViews.GenericBase
import NativeProtocolViews.GenericDerived
import NativeProtocolViews.ILeaf
import NativeProtocolViews.ValueResource

private fun <T : AutoCloseable?, R> useProtocol(value: T, block: (T) -> R): R = value.use(block)

private fun closeCheckedNative(value: System.IDisposable?) {
    if (value is AutoCloseable) value.close()
}

private fun closeCheckedConcrete(value: AutoCloseable) {
    if (value is System.IO.MemoryStream) value.close()
}

private fun closeCastStatement(value: System.IDisposable) {
    value as AutoCloseable
    value.close()
}

private fun <T> closeIntersection(value: T) where T : AutoCloseable, T : System.IDisposable = value.close()

private class NativeVocabularyResource : System.IDisposable {
    var calls = 0
    override fun Dispose() { calls++ }
    fun close() { calls += 100 }
}

private class NativeProtocolChild(counter: Counter) : GenericBase<String>("child", counter) {
    fun close(): String = "own-close"
}

private open class PrivateProtocolResource : System.IDisposable {
    var calls = 0
    override fun Dispose() { calls++ }
    private fun close() { calls += 100 }
    fun callOwnClose() { close() }
}

private fun closeCheckedPrivate(value: PrivateProtocolResource) {
    if (value is AutoCloseable) value.close()
}

private class LogicalProtocolResource : AutoCloseable {
    var calls = 0
    override fun close() { calls++ }
}

class NativeProtocolViewTests {
    @TestAttribute
    fun concreteBclUsePreservesReceiverAndDisposes() {
        val stream = System.IO.MemoryStream()
        check(stream.use { it.WriteByte(7.toUByte()); it.Length } == 1L)
        check(!stream.CanRead)
    }

    @TestAttribute
    fun nativeInterfaceAssignmentAndNullableGenericUse() {
        val counter = Counter()
        val native: System.IDisposable = GenericDerived<String>("native", counter)
        val logical: AutoCloseable = native
        check(logical === native)
        check(useProtocol(native) { it === native })
        check(counter.Calls == 1)
        val absent: System.IDisposable? = null
        check(useProtocol(absent) { it == null })
        check(counter.Calls == 1)
        logical.close()
        check(counter.Calls == 2)
    }

    @TestAttribute
    fun inheritedNativeGenericReceiversAndKotlinSubclassKeepTheirViews() {
        val counter = Counter()
        check(useProtocol(GenericDerived<String>("text", counter)) { it.Payload } == "text")
        check(GenericDerived<Int>(19, counter).use { it.Payload } == 19)
        val leaf: ILeaf = GenericDerived<String>("leaf", counter)
        check(leaf.use { 41 } == 41)
        val child = NativeProtocolChild(counter)
        check(child.close() == "own-close")
        check(child.use { it.Payload } == "child")
        check(counter.Calls == 4)
    }

    @TestAttribute
    fun nativeValueReceiversUseTheSameProtocol() {
        val counter = Counter()
        val value = ValueResource(6, counter)
        check(useProtocol(value) { it.Value } == 6)
        check(counter.Calls == 1)
    }

    @TestAttribute
    fun checkedProtocolViewsKeepTheirMembers() {
        val counter = Counter()
        closeCheckedNative(GenericDerived<String>("checked", counter))
        closeCheckedNative(null)
        closeCastStatement(GenericDerived<String>("cast", counter))
        closeIntersection(GenericDerived<String>("bounds", counter))
        check(counter.Calls == 3)
        val stream = System.IO.MemoryStream()
        closeCheckedConcrete(stream)
        check(!stream.CanRead)
    }

    @TestAttribute
    fun nativeValueBoxesAsAProtocolWithoutLosingItsDispatch() {
        val counter = Counter()
        val logical: AutoCloseable = ValueResource(9, counter)
        logical.close()
        val values = listOf<AutoCloseable>(ValueResource(10, counter))
        values.single().use { }
        check(counter.Calls == 2)
    }

    @TestAttribute
    fun privateNativeMemberDoesNotHideAnAccessibleProtocolMember() {
        val resource = PrivateProtocolResource()
        closeCheckedPrivate(resource)
        check(resource.calls == 1)
        resource.callOwnClose()
        check(resource.calls == 101)
    }

    @TestAttribute
    fun kotlinNativeImplementationRetainsIndependentMemberVocabulary() {
        val resource = NativeVocabularyResource()
        check(resource.use { it === resource })
        check(resource.calls == 1)
        resource.close()
        check(resource.calls == 101)
        val native: System.IDisposable = resource
        native.use { check(it === resource) }
        check(resource.calls == 102)
        val logical: AutoCloseable = resource
        logical.close()
        check(resource.calls == 103)
        var anonymousCalls = 0
        val anonymous = object : System.IDisposable {
            override fun Dispose() { anonymousCalls++ }
            fun close() { anonymousCalls += 100 }
        }
        anonymous.close()
        anonymous.use { }
        check(anonymousCalls == 101)
    }

    @TestAttribute
    fun logicalProtocolAndBlockFailureRetainKotlinSemantics() {
        val logical = LogicalProtocolResource()
        check(logical.use { it.calls } == 0)
        check(logical.calls == 1)
        var factoryCalls = 0
        AutoCloseable { factoryCalls++ }.use { }
        check(factoryCalls == 1)
        val counter = Counter()
        try {
            GenericDerived<String>("failure", counter).use { throw IllegalStateException("block") }
            error("block should throw")
        } catch (failure: IllegalStateException) {
            check(failure.message == "block")
        }
        check(counter.Calls == 1)
    }
}
