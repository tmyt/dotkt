import NUnit.Framework.TestAttribute
import NativeProtocolViews.Counter
import NativeProtocolViews.GenericBase
import NativeProtocolViews.GenericDerived
import NativeProtocolViews.ILeaf
import NativeProtocolViews.ValueResource

private fun <T : AutoCloseable?, R> useProtocol(value: T, block: (T) -> R): R = value.use(block)

private class NativeVocabularyResource : System.IDisposable {
    var calls = 0
    override fun Dispose() { calls++ }
    fun close() { calls += 100 }
}

private class NativeProtocolChild(counter: Counter) : GenericBase<String>("child", counter) {
    fun close(): String = "own-close"
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
