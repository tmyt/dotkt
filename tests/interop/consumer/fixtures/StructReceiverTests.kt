import NUnit.Framework.TestAttribute
import StructReceivers.Counter
import StructReceivers.GenericCounter
import StructReceivers.Holder
import StructReceivers.Key
import StructReceivers.TextKey
import kotlin.clr.byref

private fun capturedReceiverKey(holder: Holder, mutate: Boolean): Key<*> = holder.MakeKey(mutate)
private fun replacingReceiverKey(holder: Holder): Key<*> = holder.ReplaceAndMakeKey()
private fun failingReceiverKey(holder: Holder): Key<*> {
    holder.Trace += "k"
    throw IllegalStateException("key-failure")
}

private class GenericStructReceiverCaller<A>(val token: A) {
    fun <B> read(other: B, key: Key<*>): Int {
        check(other != null)
        var receiver = GenericCounter<A>(token)
        check(receiver.Read(key) == 1)
        check(receiver.Count == 1 && receiver.Value == token)
        return receiver.Read(key)
    }
}

class StructReceiverTests {
    @TestAttribute
    fun typedLocalKeepsItsMutableReceiver() {
        var counter = Counter(0)
        check(counter.Read(TextKey()) == 1)
        check(counter.Count == 1)
        counter.Increment(TextKey())
        check(counter.Count == 2)
    }

    @TestAttribute
    fun capturedLocalKeepsItsMutableReceiver() {
        var counter = Counter(0)
        val key: Key<*> = TextKey()
        check(counter.Read(key) == 1)
        check(counter.Count == 1)
        counter.Increment(key)
        check(counter.Count == 2)
    }

    @TestAttribute
    fun typedFieldIsLiveDuringTheNativeCall() {
        val holder = Holder()
        check(holder.Receiver().Counter.Observe(holder.MakeKey(true), false) == 1101)
        check(holder.Counter.Count == 11)
        check(holder.Trace == "rko")
    }

    @TestAttribute
    fun capturedFieldIsLiveDuringTheNativeCall() {
        val holder = Holder()
        check(holder.Receiver().Counter.Observe(capturedReceiverKey(holder, true), false) == 1101)
        check(holder.Counter.Count == 11)
        check(holder.Trace == "rko")
    }

    @TestAttribute
    fun receiverLocationSurvivesArgumentWritesToTheSameField() {
        val first = Holder()
        check(first.Receiver().Counter.Observe(first.ReplaceAndMakeKey(), false) == 4141)
        check(first.Counter.Count == 41 && first.Trace == "rko")
        val second = Holder()
        check(second.Receiver().Counter.Observe(replacingReceiverKey(second), false) == 4141)
        check(second.Counter.Count == 41 && second.Trace == "rko")
    }

    @TestAttribute
    fun receiverIsEvaluatedBeforeAnArgumentThrows() {
        val holder = Holder()
        try {
            holder.Receiver().Counter.Read(failingReceiverKey(holder))
            error("missing argument exception")
        } catch (failure: IllegalStateException) {
            check(failure.message == "key-failure")
        }
        check(holder.Counter.Count == 0 && holder.Trace == "rk")
    }

    @TestAttribute
    fun typedExceptionalFieldWritesAreImmediatelyVisible() {
        val holder = Holder()
        try {
            holder.Receiver().Counter.Observe(holder.MakeKey(false), true)
            error("missing native exception")
        } catch (failure: Throwable) {
            check(failure is System.InvalidOperationException && failure.message == "receiver-write:1")
        }
        check(holder.Counter.Count == 1 && holder.Trace == "rko")
    }

    @TestAttribute
    fun capturedExceptionalFieldWritesAreImmediatelyVisible() {
        val holder = Holder()
        try {
            holder.Receiver().Counter.Observe(capturedReceiverKey(holder, false), true)
            error("missing native exception")
        } catch (failure: Throwable) {
            check(failure is System.InvalidOperationException && failure.message == "receiver-write:1")
        }
        check(holder.Counter.Count == 1 && holder.Trace == "rko")
    }

    @TestAttribute
    fun exceptionalLocalWritesSurviveTypedAndCapturedCalls() {
        var first = Counter(0)
        var second = Counter(0)
        val key: Key<*> = TextKey()
        try { first.Fail(TextKey()); error("missing typed exception") }
        catch (failure: Throwable) { check(failure is System.InvalidOperationException && failure.message == "receiver-write") }
        try { second.Fail(key); error("missing captured exception") }
        catch (failure: Throwable) { check(failure is System.InvalidOperationException && failure.message == "receiver-write") }
        check(first.Count == 1 && second.Count == 1)
    }

    @TestAttribute
    fun nativeRefArgumentAliasesTheTypedReceiver() {
        var counter = Counter(0)
        check(counter.Aliased(TextKey(), byref(counter)) == 11)
        check(counter.Count == 11)
    }

    @TestAttribute
    fun supportedLiveReferenceRetainsItsNativeReceiver() {
        val holder = Holder()
        var counter by byref(Holder.Live(holder))
        val key: Key<*> = TextKey()
        check(counter.Read(TextKey()) == 1)
        check(counter.Read(key) == 2)
        check(holder.Counter.Count == 2)
    }

    @TestAttribute
    fun arrayElementKeepsItsReceiverLocationAndIndexOrder() {
        val counters = arrayOf(Counter(0), Counter(10))
        var index = 0
        val key: Key<*> = TextKey()
        check(counters[index++].Read(key) == 1)
        check(index == 1 && counters[0].Count == 1 && counters[1].Count == 10)
        check(counters[index++].Read(TextKey()) == 11)
        check(index == 2 && counters[1].Count == 11)
    }

    @TestAttribute
    fun genericOwnerAndCallerFramesRetainTheReceiverType() {
        check(GenericStructReceiverCaller("owner").read(42, TextKey()) == 2)
        check(GenericStructReceiverCaller(17).read("method", TextKey()) == 2)
    }

}
