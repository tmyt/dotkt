@file:OptIn(kotlin.concurrent.atomics.ExperimentalAtomicApi::class, kotlin.io.encoding.ExperimentalEncodingApi::class)

import NUnit.Framework.TestAttribute
import System.Threading.CountdownEvent
import System.Threading.Thread
import System.Threading.ThreadStart
import kotlin.concurrent.atomics.AtomicInt
import kotlin.io.encoding.Base64

private class PortableLazyValue(val id: Int) {
    val text = "ready"
    val number = 42
}

private fun portableUseReturn(closed: () -> Unit): Int {
    AutoCloseable(closed).use { return 42 }
}

class PortableCoreActualTests {
    @TestAttribute fun useContractAssignsOnceAndHandlesNull() {
        var closes = 0
        val assigned: String
        val result = AutoCloseable { closes++ }.use {
            assigned = "assigned"
            42
        }
        check(assigned == "assigned" && result == 42 && closes == 1)
        val absent: AutoCloseable? = null
        val fromNull: Int
        absent.use { fromNull = if (it == null) 7 else 0 }
        check(fromNull == 7)
    }

    @TestAttribute fun useDisposesAndPreservesPrimaryException() {
        var closes = 0
        val bodyFailure = IllegalStateException("body")
        val closeFailure = IllegalArgumentException("close")
        try {
            AutoCloseable { closes++; throw closeFailure }.use { throw bodyFailure }
            error("missing body failure")
        } catch (failure: IllegalStateException) {
            check(failure === bodyFailure && closes == 1)
        }
        try {
            AutoCloseable { closes++; throw closeFailure }.use { 42 }
            error("missing close failure")
        } catch (failure: IllegalArgumentException) {
            check(failure === closeFailure && closes == 2)
        }
        val native = System.IO.MemoryStream()
        check((native as AutoCloseable).use { native.CanRead })
        check(!native.CanRead)
    }

    @TestAttribute fun useClosesOnNonlocalReturn() {
        var closes = 0
        check(portableUseReturn { closes++ } == 42)
        check(closes == 1)
    }

    @TestAttribute fun lazyModesRetryAndMemoizeNullAndValues() {
        for (mode in LazyThreadSafetyMode.entries) {
            var calls = 0
            val failure = IllegalStateException("retry")
            val value = lazy(mode) {
                calls++
                if (calls == 1) throw failure
                null as String?
            }
            check(!value.isInitialized() && value.toString() == "Lazy value not initialized yet.")
            try { value.value; error("missing initializer failure") }
            catch (caught: IllegalStateException) { check(caught === failure) }
            check(!value.isInitialized())
            check(value.value == null && value.value == null)
            check(value.isInitialized() && calls == 2 && value.toString() == "null")
            val scalar = lazy(mode) { 42 }
            check(scalar.value == 42 && scalar.value == 42 && scalar.isInitialized())
        }
    }

    @TestAttribute fun publicationRacesPublishOneFullyInitializedIdentity() {
        repeat(10) {
            val arrivals = CountdownEvent(4)
            val calls = AtomicInt(0)
            val value = lazy(LazyThreadSafetyMode.PUBLICATION) {
                val id = calls.addAndFetch(1)
                arrivals.Signal()
                check(arrivals.Wait(10000)) { "publication initializers must not hold a mutual-exclusion lock" }
                PortableLazyValue(id)
            }
            val results = arrayOfNulls<PortableLazyValue>(4)
            val failures = arrayOfNulls<Throwable>(4)
            val workers = (0 until 4).map { index ->
                Thread(ThreadStart {
                    try {
                        val observed = value.value
                        check(observed.text == "ready" && observed.number == 42)
                        results[index] = observed
                    }
                    catch (failure: Throwable) { failures[index] = failure }
                }).apply { IsBackground = true }
            }
            try {
                workers.forEach { it.Start() }
                var joined = true
                workers.forEach { if (!it.Join(15000)) joined = false }
                check(joined && failures.all { it == null })
                check(calls.load() == 4)
                val winner = results[0]!!
                check(results.all { it === winner } && value.value === winner)
                check(winner.id in 1..4 && winner.text == "ready" && winner.number == 42)
            } finally {
                arrivals.Dispose()
            }
        }
    }

    @TestAttribute fun base64RetainsSchemePaddingAndDestinationPolicies() {
        val bytes = byteArrayOf(-5, -1)
        check(Base64.Default.encode(bytes) == "+/8=")
        check(Base64.UrlSafe.encode(bytes) == "-_8=")
        check(Base64.Default.encodeToByteArray(bytes).contentEquals("+/8=".encodeToByteArray()))
        val destination = ByteArray(6) { 42 }
        check(Base64.Default.encodeIntoByteArray(bytes, destination, 1) == 4)
        check(destination[0] == 42.toByte() && destination[5] == 42.toByte())
        check(destination.copyOfRange(1, 5).decodeToString() == "+/8=")
        check(Base64.UrlSafe.decode("! -_8= ?", 2, 6).contentEquals(bytes))
        val noPadding = Base64.UrlSafe.withPadding(Base64.PaddingOption.ABSENT)
        check(noPadding.encode(bytes) == "-_8" && noPadding.decode("-_8").contentEquals(bytes))
        val mime = Base64.Mime.encode(ByteArray(60))
        check("\r\n" in mime && Base64.Mime.decode(mime).contentEquals(ByteArray(60)))
    }
}
